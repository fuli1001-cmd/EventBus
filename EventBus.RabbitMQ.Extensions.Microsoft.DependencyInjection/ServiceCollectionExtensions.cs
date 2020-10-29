using EventBus.Extensions.Microsoft.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace EventBus.RabbitMQ.Extensions.Microsoft.DependencyInjection
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 注册EventBusRabbitMQ，RabbitMQ.Client.IConnectionFactory，及所有实现了IEventHandler<IEvent>的类型
        /// </summary>
        /// <param name="services">IServiceCollection实例</param>
        /// <param name="domain">事件域，同一个域中的事件才能互通</param>
        /// <param name="hostName">RabbitMQ服务地址</param>
        /// <param name="port">RabbitMQ服务端口</param>
        /// <param name="userName">RabbitMQ用户名</param>
        /// <param name="password">RabbitMQ密码</param>
        /// <returns></returns>
        public static IServiceCollection AddEventBusRabbitMQ(this IServiceCollection services, string domain, string hostName, int? port = null, string userName = null, string password = null)
        {
            AddRabbitMQ(services, hostName, port, userName, password);

            services.AddEventHandlers();

            services.AddSingleton(typeof(IEventBus), provider => new EventBusRabbitMQ(domain, provider.GetService<IConnectionFactory>(), provider, provider.GetService<ILogger<EventBusRabbitMQ>>()));

            return services;
        }

        // 注册RabbitMQ.Client的ConnectionFactory
        private static void AddRabbitMQ(IServiceCollection services, string hostName, int? port = null, string userName = null, string password = null)
        {
            var factory = new ConnectionFactory() { HostName = hostName, DispatchConsumersAsync = true };

            if (port != null)
                factory.Port = port.Value;

            if (!string.IsNullOrWhiteSpace(userName))
                factory.UserName = userName;

            if (password != null)
                factory.Password = password;

            services.AddSingleton(typeof(IConnectionFactory), factory);
        }
    }
}
