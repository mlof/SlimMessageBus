namespace SlimMessageBus.Host.Kafka;

using System.Diagnostics.CodeAnalysis;

using ConsumeResult = ConsumeResult<Ignore, byte[]>;
using IConsumer = IConsumer<Ignore, byte[]>;

public class KafkaGroupConsumer : AbstractConsumer, IKafkaCommitController
{
    private readonly SafeDictionaryWrapper<TopicPartition, IKafkaPartitionConsumer> _processors;

    private IConsumer _consumer;
    private Task _consumerTask;
    private CancellationTokenSource _consumerCts;

    public KafkaMessageBus MessageBus { get; }
    public string Group { get; }
    public IReadOnlyCollection<string> Topics { get; }

    public KafkaGroupConsumer(KafkaMessageBus messageBus, string group, IReadOnlyCollection<string> topics, Func<TopicPartition, IKafkaCommitController, IKafkaPartitionConsumer> processorFactory)
        : base(messageBus.LoggerFactory.CreateLogger<KafkaGroupConsumer>())
    {
        MessageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        Group = group ?? throw new ArgumentNullException(nameof(group));
        Topics = topics ?? throw new ArgumentNullException(nameof(topics));

        Logger.LogInformation("Creating for Group: {Group}, Topics: {Topics}", group, string.Join(", ", topics));

        _processors = new SafeDictionaryWrapper<TopicPartition, IKafkaPartitionConsumer>(tp => processorFactory(tp, this));

        _consumer = CreateConsumer(group);
    }

    #region Implementation of IAsyncDisposable

    protected override async ValueTask DisposeAsyncCore()
    {
        await base.DisposeAsyncCore();

        if (_consumerTask != null)
        {
            await Stop().ConfigureAwait(false);
        }

        // dispose processors
        foreach (var p in _processors.ClearAndSnapshot())
        {
            p.DisposeSilently("processor", Logger);
        }

        // dispose the consumer
        _consumer?.DisposeSilently("consumer", Logger);
        _consumer = null;
    }

    #endregion

    protected IConsumer CreateConsumer(string group)
    {
        var config = new ConsumerConfig
        {
            GroupId = group,
            BootstrapServers = MessageBus.ProviderSettings.BrokerList
        };
        MessageBus.ProviderSettings.ConsumerConfig(config);

        // ToDo: add support for auto commit
        config.EnableAutoCommit = false;
        // Notify when we reach EoF, so that we can do a manual commit
        config.EnablePartitionEof = true;

        var consumer = MessageBus.ProviderSettings.ConsumerBuilderFactory(config)
            .SetStatisticsHandler((_, json) => OnStatistics(json))
            .SetPartitionsAssignedHandler((_, partitions) => OnPartitionAssigned(partitions))
            .SetPartitionsRevokedHandler((_, partitions) => OnPartitionRevoked(partitions))
            .SetOffsetsCommittedHandler((_, offsets) => OnOffsetsCommitted(offsets))
            .Build();

        return consumer;
    }

    protected override Task OnStart()
    {
        if (_consumerTask != null)
        {
            throw new MessageBusException($"Consumer for group {Group} already started");
        }

        _consumerCts = new CancellationTokenSource();
        _consumerTask = Task.Factory.StartNew(ConsumerLoop, _consumerCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        return Task.CompletedTask;
    }

    /// <summary>
    /// The consumer group loop
    /// </summary>
    protected async virtual Task ConsumerLoop()
    {
        Logger.LogInformation("Group [{Group}]: Subscribing to topics: {Topics}", Group, string.Join(", ", Topics));
        _consumer.Subscribe(Topics);

        Logger.LogInformation("Group [{Group}]: Consumer loop started", Group);
        try
        {
            try
            {
                for (var cancellationToken = _consumerCts.Token; !cancellationToken.IsCancellationRequested;)
                {
                    try
                    {
                        Logger.LogTrace("Group [{Group}]: Polling consumer", Group);
                        var consumeResult = _consumer.Consume(cancellationToken);
                        if (consumeResult.IsPartitionEOF)
                        {
                            OnPartitionEndReached(consumeResult.TopicPartitionOffset);
                        }
                        else
                        {
                            await OnMessage(consumeResult).ConfigureAwait(false);
                        }
                    }
                    catch (ConsumeException e)
                    {
                        var pollRetryInterval = MessageBus.ProviderSettings.ConsumerPollRetryInterval;

                        Logger.LogError(e, "Group [{Group}]: Error occured while polling new messages (will retry in {RetryInterval}) - {Reason}", Group, pollRetryInterval, e.Error.Reason);
                        await Task.Delay(pollRetryInterval, _consumerCts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }

            Logger.LogInformation("Group [{Group}]: Unsubscribing from topics", Group);
            _consumer.Unsubscribe();

            if (MessageBus.ProviderSettings.EnableCommitOnBusStop)
            {
                OnClose();
            }

            // Ensure the consumer leaves the group cleanly and final offsets are committed.
            _consumer.Close();
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Group [{Group}]: Error occured in group loop (terminated)", Group);
        }
        finally
        {
            Logger.LogInformation("Group [{Group}]: Consumer loop finished", Group);
        }
    }

    protected override async Task OnStop()
    {
        if (_consumerTask == null)
        {
            throw new MessageBusException($"Consumer for group {Group} not yet started");
        }

        _consumerCts.Cancel();
        try
        {
            await _consumerTask.ConfigureAwait(false);
        }
        finally
        {
            _consumerTask = null;

            _consumerCts.DisposeSilently();
            _consumerCts = null;
        }
    }

    protected virtual void OnPartitionAssigned([NotNull] ICollection<TopicPartition> partitions)
    {
        // Ensure processors exist for each assigned topic-partition
        foreach (var partition in partitions)
        {
            Logger.LogDebug("Group [{Group}]: Assigned partition, Topic: {Topic}, Partition: {Partition}", Group, partition.Topic, partition.Partition);

            var processor = _processors[partition];
            processor.OnPartitionAssigned(partition);
        }
    }

    protected virtual void OnPartitionRevoked([NotNull] ICollection<TopicPartitionOffset> partitions)
    {
        foreach (var partition in partitions)
        {
            Logger.LogDebug("Group [{Group}]: Revoked Topic: {Topic}, Partition: {Partition}, Offset: {Offset}", Group, partition.Topic, partition.Partition, partition.Offset);

            var processor = _processors[partition.TopicPartition];
            processor.OnPartitionRevoked();
        }
    }

    protected virtual void OnPartitionEndReached([NotNull] TopicPartitionOffset offset)
    {
        Logger.LogDebug("Group [{Group}]: Reached end of partition, Topic: {Topic}, Partition: {Partition}, Offset: {Offset}", Group, offset.Topic, offset.Partition, offset.Offset);

        var processor = _processors[offset.TopicPartition];
        processor.OnPartitionEndReached(offset);
    }

    protected async virtual ValueTask OnMessage([NotNull] ConsumeResult message)
    {
        Logger.LogDebug("Group [{Group}]: Received message with Topic: {Topic}, Partition: {Partition}, Offset: {Offset}, payload size: {MessageSize}", Group, message.Topic, message.Partition, message.Offset, message.Message.Value?.Length ?? 0);

        var processor = _processors[message.TopicPartition];
        await processor.OnMessage(message).ConfigureAwait(false);
    }

    protected virtual void OnOffsetsCommitted([NotNull] CommittedOffsets e)
    {
        if (e.Error.IsError || e.Error.IsFatal)
        {
            Logger.LogWarning("Group [{Group}]: Failed to commit offsets: [{Offsets}], error: {error}", Group, string.Join(", ", e.Offsets), e.Error.Reason);
        }
        else
        {
            Logger.LogTrace("Group [{Group}]: Successfully committed offsets: [{Offsets}]", Group, string.Join(", ", e.Offsets));
        }
    }

    protected virtual void OnClose()
    {
        var processors = _processors.Snapshot();
        foreach (var processor in processors)
        {
            processor.OnClose();
        }
    }

    protected virtual void OnStatistics(string json)
    {
        Logger.LogTrace("Group [{Group}]: Statistics: {statistics}", Group, json);
    }

    #region Implementation of IKafkaCoordinator

    public void Commit(TopicPartitionOffset offset)
    {
        Logger.LogDebug("Group [{Group}]: Commit Offset, Topic: {Topic}, Partition: {Partition}, Offset: {Offset}", Group, offset.Topic, offset.Partition, offset.Offset);
        _consumer.Commit(new[] { offset });
    }

    #endregion
}