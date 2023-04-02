﻿namespace Sample.AsyncApi.Service.Messages;

public class CustomerCreatedEventConsumer : IConsumer<CustomerCreatedEvent>
{
    /// <summary>
    /// Upon the <see cref="CustomerCreatedEvent"/> will store it with the database.
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    public Task OnHandle(CustomerCreatedEvent message)
    {
        throw new NotImplementedException();
    }
}