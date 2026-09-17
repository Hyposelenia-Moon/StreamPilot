namespace StreamPilot.Core.Runtime;

/// <summary>
/// 极简服务注册表：替代 Microsoft.Extensions.DependencyInjection（见 docs/adr/0001-technology-stack.md）。
/// </summary>
/// <remarks>
/// 只支持单例注册与按接口解析，够用且不引入任何第三方包。
/// 线程安全：注册完成后只读，读取使用不可变字典。
/// </remarks>
public sealed class ServiceRegistry
{
    private readonly Dictionary<Type, Func<ServiceRegistry, object>> _factories = [];
    private readonly Dictionary<Type, object> _instances = [];
    private readonly Lock _gate = new();

    /// <summary>注册一个单例服务。</summary>
    /// <typeparam name="TService">服务契约类型。</typeparam>
    /// <param name="factory">工厂函数，接收注册表以便解析依赖。</param>
    /// <returns>注册表本身，便于链式调用。</returns>
    public ServiceRegistry RegisterSingleton<TService>(Func<ServiceRegistry, TService> factory)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            _factories[typeof(TService)] = registry => factory(registry);
        }

        return this;
    }

    /// <summary>注册一个已构造好的单例实例。</summary>
    /// <typeparam name="TService">服务契约类型。</typeparam>
    /// <param name="instance">实例。</param>
    /// <returns>注册表本身。</returns>
    public ServiceRegistry RegisterInstance<TService>(TService instance)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (_gate)
        {
            _instances[typeof(TService)] = instance;
        }

        return this;
    }

    /// <summary>解析服务；未注册时抛出带服务名的异常。</summary>
    /// <typeparam name="TService">服务契约类型。</typeparam>
    /// <returns>服务实例。</returns>
    /// <exception cref="InvalidOperationException">服务未注册时抛出。</exception>
    public TService GetRequired<TService>()
        where TService : class
    {
        Type key = typeof(TService);
        lock (_gate)
        {
            if (_instances.TryGetValue(key, out object? existing))
            {
                return (TService)existing;
            }

            if (!_factories.TryGetValue(key, out Func<ServiceRegistry, object>? factory))
            {
                throw new InvalidOperationException($"服务未注册：{key.FullName}。请在组合根中注册该服务。");
            }

            object created = factory(this);
            _instances[key] = created;
            return (TService)created;
        }
    }

    /// <summary>尝试解析服务。</summary>
    /// <typeparam name="TService">服务契约类型。</typeparam>
    /// <param name="service">解析结果。</param>
    /// <returns>解析成功返回 <see langword="true"/>。</returns>
    public bool TryGet<TService>(out TService? service)
        where TService : class
    {
        Type key = typeof(TService);
        lock (_gate)
        {
            if (!_instances.ContainsKey(key) && !_factories.ContainsKey(key))
            {
                service = null;
                return false;
            }
        }

        service = GetRequired<TService>();
        return true;
    }
}
