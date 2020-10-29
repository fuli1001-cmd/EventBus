# EventBus

An event bus library for .NET and .NET core, currently implemented by RabbitMQ.

## Nuget Packages

### Fuli.EventBus

Contains only interfaces, use with it's implemention EventBus.RabbitMQ.

### Fuli.EventBus.RabbitMQ

EventBus RabbitMQ implementation.

### Fuli.EventBus.Extensions.Microsoft.DependencyInjection

EventBus extensions for .NET Core

### Fuli.EventBus.RabbitMQ.Extensions.Microsoft.DependencyInjection

EventBus.RabbitMQ extensions for .NET Core

## Installation

dotnet add package Fuli.EventBus.RabbitMQ.Extensions.Microsoft.DependencyInjection

## Usage

1. Add EventBus service to IoC comtainer, the first parameter "EventBusTestDomain" is an application domain, events are handled in the same doamin, the second parameter "localhost" is the RabbitMQ server, you can also pass in RabbitMQ server port, userName and password as well.

    ```c#
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddEventBusRabbitMQ("EventBusTestDomain", "localhost");
    }
    ```

2. Define an event, event is a POCO class which derive from `IEvent`.

    ```c#
    public class SomethingHappenedEvent : IEvent
    {
        public string Message { get; set; }
    }
    ```

3. Define an event handler class implement `IEventHandler<T>`, where `T` is an event class, write your logic in HandleAsync method.

    ```c#
    public class SomethingHappenedEventHandler : IEventHandler<SomethingHappenedEvent>
    {
        private readonly ILogger<SomethingHappenedEventHandler> _logger;

        public SomethingHappenedEventHandler(ILogger<SomethingHappenedEventHandler> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task HandleAsync(TestEvent @event)
        {
            _logger.LogInformation("***** Handling SomethingHappenedEvent");

            await Task.Delay(new Random().Next(1) * 1000);
            _logger.LogInformation(@event.Message);

            _logger.LogInformation("***** SomethingHappenedEvent handled");
        }
    }
    ```

4. Publish an event, _eventBus is an `IEventBus` instance, inject it into your class and using it's PublishAsync method to publish the event. The event will be passed to it's handlers for processing, in case of the handler's transient failure, `IEventBus` will try 4 times with exponential backoff strategy, if it's still failed, the event message will be transferred to the error center, where you can resend it when the handler service is back online. Below shows a background service which uses IEventBus to publish SomethingHappenedEvent.

    ```c#
    public class BgService : BackgroundService
    {
        private readonly IEventBus _eventBus;

        public BgService(IEventBus eventBus)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var @event = new SomethingHappenedEvent { Message = "Hello EventBus!" };
            await _eventBus.PublishAsync(@event);

            return Task.CompletedTask;
        }
    }
    ```
