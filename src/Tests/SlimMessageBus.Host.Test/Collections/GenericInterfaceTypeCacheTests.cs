﻿namespace SlimMessageBus.Host.Test.Collections;

using SlimMessageBus.Host.Collections;
using SlimMessageBus.Host.DependencyResolver;
using SlimMessageBus.Host.Interceptor;

public class GenericInterfaceTypeCacheTests
{
    private readonly Mock<IConsumerInterceptor<SomeMessage>> consumerInterceptorMock;
    private readonly Mock<IDependencyResolver> scopeMock;

    public GenericInterfaceTypeCacheTests()
    {
        consumerInterceptorMock = new Mock<IConsumerInterceptor<SomeMessage>>();

        scopeMock = new Mock<IDependencyResolver>();
        scopeMock.Setup(x => x.Resolve(typeof(IEnumerable<IConsumerInterceptor<SomeMessage>>))).Returns(() => new[] { consumerInterceptorMock.Object });
    }

    [Fact]
    public void When_ResolveAll_Given_OneRegistrationExists_Then_ReturnsThatRegistration()
    {
        // arrange
        var subject = new GenericInterfaceTypeCache(typeof(IConsumerInterceptor<>), nameof(IConsumerInterceptor<object>.OnHandle));

        // act
        var interceptors = subject.ResolveAll(scopeMock.Object, typeof(SomeMessage));

        // assert
        scopeMock.Verify(x => x.Resolve(typeof(IEnumerable<IConsumerInterceptor<SomeMessage>>)), Times.Once);
        scopeMock.VerifyNoOtherCalls();

        interceptors.Should().HaveCount(1);
        interceptors.Should().Contain(consumerInterceptorMock.Object);
    }

    [Fact]
    public void When_ResolveAll_Given_NoRegistrations_Then_ReturnsNull()
    {
        // arrange
        var subject = new GenericInterfaceTypeCache(typeof(IConsumerInterceptor<>), nameof(IConsumerInterceptor<object>.OnHandle));

        scopeMock.Setup(x => x.Resolve(typeof(IEnumerable<IConsumerInterceptor<SomeMessage>>))).Returns(() => Enumerable.Empty<object>());

        // act
        var interceptors = subject.ResolveAll(scopeMock.Object, typeof(SomeMessage));

        // assert
        scopeMock.Verify(x => x.Resolve(typeof(IEnumerable<IConsumerInterceptor<SomeMessage>>)), Times.Once);
        scopeMock.VerifyNoOtherCalls();

        interceptors.Should().BeNull();
    }

    [Fact]
    public async Task When_Get_Then_ReturnsValidMethodAndInterceptorGenericType()
    {
        // arrange
        var message = new SomeMessage();
        Func<Task> next = () => Task.CompletedTask;
        var headers = new Dictionary<string, object>();
        var consumer = new object();
        var consumerContext = new ConsumerContext();

        consumerInterceptorMock.Setup(x => x.OnHandle(message, next, consumerContext)).Returns(Task.CompletedTask);

        var subject = new GenericInterfaceTypeCache(typeof(IConsumerInterceptor<>), nameof(IConsumerInterceptor<object>.OnHandle));

        // act
        var interceptorType = subject.Get(typeof(SomeMessage));

        var task = (Task)interceptorType.Method.Invoke(consumerInterceptorMock.Object, new object[] { message, next, consumerContext });
        await task;

        // assert
        interceptorType.GenericType.Should().Be(typeof(IConsumerInterceptor<SomeMessage>));
        interceptorType.MessageType.Should().Be(typeof(SomeMessage));

        consumerInterceptorMock.Verify(x => x.OnHandle(message, next, consumerContext), Times.Once);
        consumerInterceptorMock.VerifyNoOtherCalls();
    }
}
