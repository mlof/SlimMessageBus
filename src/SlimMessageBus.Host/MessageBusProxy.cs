﻿namespace SlimMessageBus.Host;

/// <summary>
/// Proxy to the <see cref="IMessageBusBase"/> that introduces its own <see cref="IDependencyResolver"/> for dependency lookup.
/// </summary>
public class MessageBusProxy : IMessageBus
{
    /// <summary>
    /// The target of this proxy (the singleton master bus).
    /// </summary>
    public IMessageBusProducer Target { get; }
    public IDependencyResolver DependencyResolver { get; }

    public MessageBusProxy(IMessageBusProducer target, IDependencyResolver dependencyResolver)
    {
        Target = target;
        DependencyResolver = dependencyResolver;
    }

    #region Implementation of IMessageBus

    public void Dispose()
    {
        // Nothing to dispose
    }

    #region Implementation of IPublishBus

    public Task Publish<TMessage>(TMessage message, string path = null, IDictionary<string, object> headers = null, CancellationToken cancellationToken = default)
        => Target.Publish(message, path: path, headers: headers, cancellationToken: cancellationToken, currentDependencyResolver: DependencyResolver);

    #endregion

    #region Implementation of IRequestResponseBus

    public Task<TResponseMessage> Send<TResponseMessage>(IRequestMessage<TResponseMessage> request, string path = null, IDictionary<string, object> headers = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
        => Target.SendInternal<TResponseMessage>(request, timeout: timeout, path: path, headers: headers, cancellationToken, currentDependencyResolver: DependencyResolver);

    public Task<TResponseMessage> Send<TResponseMessage, TRequestMessage>(TRequestMessage request, string path = null, IDictionary<string, object> headers = null, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
        => Target.SendInternal<TResponseMessage>(request, timeout: timeout, path: path, headers: headers, cancellationToken, currentDependencyResolver: DependencyResolver);

    #endregion

    #endregion
}