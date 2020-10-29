using Microsoft.Extensions.DependencyInjection;
using Scrutor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace EventBus.Extensions.Microsoft.DependencyInjection
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 注册所有实现了IEventHandler<IEvent>的类型
        /// </summary>
        /// <param name="services">IServiceCollection实例</param>
        /// <returns></returns>
        public static IServiceCollection AddEventHandlers(this IServiceCollection services)
        {
            services.Scan(scan => scan.
            FromApplicationDependencies().
            AddClasses(x => x.AssignableTo(typeof(IEventHandler<>))).
            UsingRegistrationStrategy(RegistrationStrategy.Skip).
            AsImplementedInterfaces().WithTransientLifetime());

            return services;
        }
    }
}
