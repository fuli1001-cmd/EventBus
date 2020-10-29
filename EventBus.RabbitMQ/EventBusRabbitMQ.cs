using EventBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Polly;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EventBus.RabbitMQ
{
    /// <summary>
    /// Using RabbitMQ to implement event bus
    /// </summary>
    public class EventBusRabbitMQ : IEventBus
    {
        private readonly string _exchangeName;
        private readonly ILogger<EventBusRabbitMQ> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IConnectionFactory _connectionFactory;

        private IConnection _publisherConnection;
        private IConnection _consummerConnection;
        private IModel _consummerChannel;
        private string _errorExchangeName;
        private string _prefix;

        // store event and it's assembly
        private Dictionary<string, Assembly> _dicEventAssembly = new Dictionary<string, Assembly>();

        public EventBusRabbitMQ(string domain, IConnectionFactory connectionFactory, IServiceProvider serviceProvider, ILogger<EventBusRabbitMQ> logger)
        {
            _exchangeName = domain ?? throw new ArgumentNullException(nameof(domain));
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            Start();
        }

        private void Start()
        {
            var eventTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetTypes())
            .Where(x => !x.IsInterface && !x.IsAbstract && x.IsPublic && typeof(IEvent).IsAssignableFrom(x))
            .ToList();

            InitComsumer(eventTypes);

            InitPublisher();

            InitError();
        }

        // use error exchange to store events which had error when handle
        private void InitError()
        {
            _errorExchangeName = "error." + _exchangeName;
            _consummerChannel.ExchangeDeclare(exchange: _errorExchangeName, type: ExchangeType.Fanout, durable: true, autoDelete: false);
        }

        // init connection for publisher
        private void InitPublisher()
        {
            _publisherConnection = _connectionFactory.CreateConnection();
        }

        // init consumer, declare comsumner queue and bind to events, start consume 
        private void InitComsumer(List<Type> eventTypes)
        {
            _consummerConnection = _connectionFactory.CreateConnection();
            _consummerChannel = _consummerConnection.CreateModel();
            _consummerChannel.ExchangeDeclare(exchange: _exchangeName, type: ExchangeType.Direct, durable: true, autoDelete: false);

            _prefix = Process.GetCurrentProcess().ProcessName + ".";
            var queueName = _prefix + _exchangeName;
            _consummerChannel.QueueDeclare(queue: queueName, durable: true, exclusive: false, autoDelete: false);

            // bind event (which has handler) name
            eventTypes.ForEach(eventType =>
            {
                var handlerType = typeof(IEventHandler<>).MakeGenericType(eventType);
                if (_serviceProvider.GetService(handlerType) != null)
                {
                    // record event and it's assembly so that we can invoke it's handler when event received
                    _dicEventAssembly.Add(eventType.FullName, eventType.Assembly);

                    // bind event name
                    _consummerChannel.QueueBind(queue: queueName, exchange: _exchangeName, routingKey: eventType.FullName);

                    // bind event name with process name prefix to receive event by client
                    // this is used for receiving re-queued event from error center
                    _consummerChannel.QueueBind(queue: queueName, exchange: _exchangeName, routingKey: _prefix + eventType.FullName);

                    _logger.LogDebug("----- Registered event {Event}", eventType.FullName);
                }
                else
                {
                    _consummerChannel.QueueUnbind(queue: queueName, exchange: _exchangeName, routingKey: eventType.FullName);
                    _consummerChannel.QueueUnbind(queue: queueName, exchange: _exchangeName, routingKey: _prefix + eventType.FullName);

                    _logger.LogDebug("----- Unregistered event {Event}", eventType.FullName);
                }
            });

            var consumer = new AsyncEventingBasicConsumer(_consummerChannel);
            consumer.Received += Consumer_Received;

            _consummerChannel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);
        }

        // message received, call event handler
        private async Task Consumer_Received(object sender, BasicDeliverEventArgs ea)
        {
            string eventTypeName = ea.RoutingKey;

            // event from error center
            if (!_dicEventAssembly.ContainsKey(eventTypeName) && eventTypeName.StartsWith(_prefix)) 
                eventTypeName = eventTypeName.Substring(_prefix.Length);

            if (_dicEventAssembly.ContainsKey(eventTypeName))
            {
                var eventType = _dicEventAssembly[eventTypeName].GetType(eventTypeName);

                var handlerType = typeof(IEventHandler<>).MakeGenericType(eventType);
                var handler = _serviceProvider.GetService(handlerType);
                var methodInfo = handlerType.GetMethod("HandleAsync", new Type[] { eventType });

                var message = Encoding.UTF8.GetString(ea.Body.ToArray());
                var @event = JsonConvert.DeserializeObject(message, eventType);

                _logger.LogInformation("----- Received event {@Event}", @event);

                if (await HandleEventAsync(methodInfo, handler, @event))
                {
                    _logger.LogInformation("----- Event {Event} handled.", eventType.Name);
                }
                else
                {
                    // finally failed, move the event to error center.
                    Publish((IEvent)@event, _errorExchangeName, _prefix);

                    _logger.LogInformation("----- Moved event {@Event} to error center.", @event);
                }
            }

            ((AsyncEventingBasicConsumer)sender).Model.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
        }

        // use exponential backoff strategy to execute event handler, improve resiliency
        private async Task<bool> HandleEventAsync(MethodInfo methodInfo, object handler, object @event)
        {
            var count = 1;
            var policy = Policy.Handle<Exception>().WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

            try
            {
                await policy.ExecuteAsync(async () =>
                {
                    _logger.LogInformation("----- Calling handler of {@Event}, count: {Count}", @event, count++);
                    await (Task)methodInfo.Invoke(handler, new object[] { @event });
                });
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError("----- Handler of event {@Event} failed {Count} times. {Exception}", @event, count - 1, ex);
                return false;
            }
        }

        public void Publish(IEvent @event)
        {
            Publish(@event, _exchangeName);
        }

        private void Publish(IEvent @event, string exchangeName, string routingKeyPrefix = "")
        {
            using (var channel = _publisherConnection.CreateModel())
            {
                var message = JsonConvert.SerializeObject(@event);
                var body = Encoding.UTF8.GetBytes(message);

                var properties = channel.CreateBasicProperties();
                properties.Persistent = true;

                channel.BasicPublish(exchange: exchangeName, routingKey: routingKeyPrefix + @event.GetType().FullName, basicProperties: properties, body: body);

                if (exchangeName != _errorExchangeName)
                    _logger.LogInformation("----- published event {@Event}", @event);
            }
        }
    }
}
