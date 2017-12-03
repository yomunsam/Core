﻿/*
 * This file is part of the CatLib package.
 *
 * (c) Yu Bin <support@catlib.io>
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 *
 * Document: http://catlib.io/
 */

using System;
using System.Collections.Generic;
using System.Reflection;

namespace CatLib
{
    ///<summary>
    /// 依赖注入容器
    /// </summary>
    public class Container : IContainer
    {
        /// <summary>
        /// 服务所绑定的相关数据，记录了服务的关系
        /// </summary>
        private readonly Dictionary<string, BindData> binds;

        ///<summary>
        /// 如果所属服务是静态的那么构建后将会储存在这里
        ///</summary>
        private readonly Dictionary<string, object> instances;

        ///<summary>
        /// 服务的别名(key: 别名 , value: 映射的服务名)
        ///</summary>
        private readonly Dictionary<string, string> aliases;

        /// <summary>
        /// 可以通过服务的真实名字来查找别名
        /// </summary>
        private readonly Dictionary<string, List<string>> aliasesReverse;

        /// <summary>
        /// 服务标记，一个标记允许标记多个服务
        /// </summary>
        private readonly Dictionary<string, List<string>> tags;

        /// <summary>
        /// 服务构建时的修饰器
        /// </summary>
        private readonly List<Func<IBindData, object, object>> resolving;

        /// <summary>
        /// 静态服务释放时的修饰器
        /// </summary>
        private readonly List<Action<IBindData, object>> release;

        /// <summary>
        /// 类型查询回调
        /// 当类型无法被解决时会尝试去开发者提供的查询器中查询类型
        /// </summary>
        private readonly SortSet<Func<string, Type>, int> findType;

        /// <summary>
        /// 类型查询回调缓存
        /// </summary>
        private readonly Dictionary<string, Type> findTypeCache;

        /// <summary>
        /// 已经被解决过的服务名
        /// </summary>
        private readonly Dictionary<string, bool> resolved;

        /// <summary>
        /// 重定义事件
        /// </summary>
        private readonly Dictionary<string, List<Action<IContainer, object>>> rebound;

        /// <summary>
        /// 方法容器
        /// </summary>
        private readonly MethodContainer methodContainer;

        /// <summary>
        /// 同步锁
        /// </summary>
        private readonly object syncRoot = new object();

        /// <summary>
        /// 注入目标
        /// </summary>
        private readonly Type injectTarget;

        /// <summary>
        /// 编译堆栈
        /// </summary>
        private readonly Stack<string> buildStack;

        /// <summary>
        /// 用户参数堆栈
        /// </summary>
        private readonly Stack<object[]> userParamsStack;

        /// <summary>
        /// 构造一个容器
        /// </summary>
        /// <param name="prime">初始预计服务数量</param>
        public Container(int prime = 32)
        {
            prime = Math.Max(8, prime);
            tags = new Dictionary<string, List<string>>((int)(prime * 0.25));
            aliases = new Dictionary<string, string>(prime * 4);
            aliasesReverse = new Dictionary<string, List<string>>(prime * 4);
            instances = new Dictionary<string, object>(prime * 4);
            binds = new Dictionary<string, BindData>(prime * 4);
            resolving = new List<Func<IBindData, object, object>>((int)(prime * 0.25));
            release = new List<Action<IBindData, object>>((int)(prime * 0.25));
            resolved = new Dictionary<string, bool>(prime * 4);
            findType = new SortSet<Func<string, Type>, int>();
            findTypeCache = new Dictionary<string, Type>(prime * 4);
            rebound = new Dictionary<string, List<Action<IContainer, object>>>(prime);
            buildStack = new Stack<string>(32);
            userParamsStack = new Stack<object[]>(32);

            injectTarget = typeof(InjectAttribute);
            methodContainer = new MethodContainer(this);
        }

        /// <summary>
        /// 为一个及以上的服务定义一个标记
        /// 如果标记已经存在那么服务会被追加进列表
        /// </summary>
        /// <param name="tag">标记名</param>
        /// <param name="service">服务名或者别名</param>
        /// <exception cref="ArgumentNullException"><paramref name="service"/>为<c>null</c>或者<paramref name="service"/>中的元素为<c>null</c>或者空字符串</exception>
        public void Tag(string tag, params string[] service)
        {
            Guard.NotEmptyOrNull(tag, "tag");
            Guard.NotNull(service, "service");
            Guard.CountGreaterZero(service, "service");
            Guard.ElementNotEmptyOrNull(service, "service");

            lock (syncRoot)
            {
                List<string> list;
                if (tags.TryGetValue(tag, out list))
                {
                    list.AddRange(service);
                }
                else
                {
                    list = new List<string>(service);
                    tags.Add(tag, list);
                }
            }
        }

        /// <summary>
        /// 根据标记名生成标记所对应的所有服务实例
        /// </summary>
        /// <param name="tag">标记名</param>
        /// <returns>将会返回标记所对应的所有服务实例</returns>
        /// <exception cref="RuntimeException"><paramref name="tag"/>不存在</exception>
        /// <exception cref="ArgumentNullException"><paramref name="tag"/>为<c>null</c>或者空字符串</exception>
        public object[] Tagged(string tag)
        {
            Guard.NotEmptyOrNull(tag, "tag");
            lock (syncRoot)
            {
                List<string> serviceList;
                if (!tags.TryGetValue(tag, out serviceList))
                {
                    throw new RuntimeException("Tag [" + tag + "] is not exist.");
                }

                var result = new List<object>();

                foreach (var tagService in serviceList)
                {
                    result.Add(Make(tagService));
                }

                return result.ToArray();
            }
        }

        /// <summary>
        /// 获取服务的绑定数据,如果绑定不存在则返回null（只有进行过bind才视作绑定）
        /// </summary>
        /// <param name="service">服务名或别名</param>
        /// <returns>服务绑定数据或者null</returns>
        /// <exception cref="ArgumentNullException"><paramref name="service"/>为<c>null</c>或者空字符串</exception>
        public IBindData GetBind(string service)
        {
            Guard.NotEmptyOrNull(service, "service");
            lock (syncRoot)
            {
                service = NormalizeService(service);
                service = AliasToService(service);
                BindData bindData;
                return binds.TryGetValue(service, out bindData) ? bindData : null;
            }
        }

        /// <summary>
        /// 是否已经绑定了服务（只有进行过bind才视作绑定）
        /// </summary>
        /// <param name="service">服务名或别名</param>
        /// <returns>服务是否被绑定</returns>
        public bool HasBind(string service)
        {
            return GetBind(service) != null;
        }

        /// <summary>
        /// 是否已经实例静态化
        /// </summary>
        /// <param name="service">服务名或别名</param>
        /// <returns>是否已经静态化</returns>
        public bool HasInstance(string service)
        {
            Guard.NotEmptyOrNull(service, "service");
            lock (syncRoot)
            {
                service = NormalizeService(service);
                service = AliasToService(service);
                return instances.ContainsKey(service);
            }
        }

        /// <summary>
        /// 服务是否已经被解决过
        /// </summary>
        /// <param name="service">服务名或别名</param>
        /// <returns>是否已经被解决过</returns>
        public bool IsResolved(string service)
        {
            Guard.NotEmptyOrNull(service, "service");
            lock (syncRoot)
            {
                service = NormalizeService(service);
                service = AliasToService(service);
                return resolved.ContainsKey(service) || instances.ContainsKey(service);
            }
        }

        /// <summary>
        /// 是否可以生成服务
        /// </summary>
        /// <param name="service">服务名或者别名</param>
        /// <returns>是否可以生成服务</returns>
        public bool CanMake(string service)
        {
            Guard.NotEmptyOrNull(service, "service");
            lock (syncRoot)
            {
                service = NormalizeService(service);
                service = AliasToService(service);
                return HasBind(service) || HasInstance(service) || GetServiceType(service) != null;
            }
        }

        /// <summary>
        /// 服务是否是静态化的,如果服务不存在也将返回false
        /// </summary>
        /// <param name="service">服务名或者别名</param>
        /// <returns>是否是静态化的</returns>
        public bool IsStatic(string service)
        {
            var bind = GetBind(service);
            return bind != null && bind.IsStatic;
        }

        /// <summary>
        /// 是否是别名
        /// </summary>
        /// <param name="name">名字</param>
        /// <returns>是否是别名</returns>
        public bool IsAlias(string name)
        {
            return aliases.ContainsKey(name);
        }

        /// <summary>
        /// 为服务设定一个别名
        /// </summary>
        /// <param name="alias">别名</param>
        /// <param name="service">映射到的服务名</param>
        /// <returns>当前容器对象</returns>
        /// <exception cref="RuntimeException"><paramref name="alias"/>别名冲突或者<paramref name="service"/>的绑定与实例都不存在</exception>
        /// <exception cref="ArgumentNullException"><paramref name="alias"/>,<paramref name="service"/>为<c>null</c>或者空字符串</exception>
        public IContainer Alias(string alias, string service)
        {
            Guard.NotEmptyOrNull(alias, "alias");
            Guard.NotEmptyOrNull(service, "service");

            if (alias == service)
            {
                throw new RuntimeException("Alias is Same as Service Name: [" + alias + "].");
            }

            alias = NormalizeService(alias);
            service = NormalizeService(service);

            lock (syncRoot)
            {
                if (aliases.ContainsKey(alias))
                {
                    throw new RuntimeException("Alias [" + alias + "] is already exists.");
                }
                if (!binds.ContainsKey(service) && !instances.ContainsKey(service))
                {
                    throw new RuntimeException("You must Bind() or Instance() serivce before you can call Alias().");
                }

                aliases.Add(alias, service);

                List<string> serviceList;
                if (aliasesReverse.TryGetValue(service, out serviceList))
                {
                    serviceList.Add(alias);
                }
                else
                {
                    aliasesReverse.Add(service, new List<string> { alias });
                }
            }

            return this;
        }

        /// <summary>
        /// 如果服务不存在那么则绑定服务
        /// </summary>
        /// <param name="service">服务名</param>
        /// <param name="concrete">服务实现</param>
        /// <param name="isStatic">服务是否是静态的</param>
        /// <returns>服务绑定数据</returns>
        public IBindData BindIf(string service, Func<IContainer, object[], object> concrete, bool isStatic)
        {
            var bind = GetBind(service);
            return bind ?? Bind(service, concrete, isStatic);
        }

        /// <summary>
        /// 如果服务不存在那么则绑定服务
        /// </summary>
        /// <param name="service">服务名</param>
        /// <param name="concrete">服务实现</param>
        /// <param name="isStatic">服务是否是静态的</param>
        /// <returns>服务绑定数据</returns>
        public IBindData BindIf(string service, Type concrete, bool isStatic)
        {
            var bind = GetBind(service);
            return bind ?? Bind(service, concrete, isStatic);
        }

        /// <summary>
        /// 绑定一个服务
        /// </summary>
        /// <param name="service">服务名</param>
        /// <param name="concrete">服务实现</param>
        /// <param name="isStatic">服务是否静态化</param>
        /// <returns>服务绑定数据</returns>
        /// <exception cref="ArgumentNullException"><paramref name="concrete"/>为<c>null</c>或者空字符串</exception>
        public IBindData Bind(string service, Type concrete, bool isStatic)
        {
            Guard.NotNull(concrete, "concrete");
            return Bind(service, (c, param) =>
            {
                var container = (Container)c;
                return container.Resolve(service, concrete, false, param);
            }, isStatic);
        }

        /// <summary>
        /// 绑定一个服务
        /// </summary>
        /// <param name="service">服务名</param>
        /// <param name="concrete">服务实现</param>
        /// <param name="isStatic">服务是否静态化</param>
        /// <returns>服务绑定数据</returns>
        /// <exception cref="RuntimeException"><paramref name="service"/>绑定冲突</exception>
        /// <exception cref="ArgumentNullException"><paramref name="concrete"/>为<c>null</c></exception>
        public IBindData Bind(string service, Func<IContainer, object[], object> concrete, bool isStatic)
        {
            Guard.NotEmptyOrNull(service, "service");
            Guard.NotNull(concrete, "concrete");
            service = NormalizeService(service);
            lock (syncRoot)
            {
                if (binds.ContainsKey(service))
                {
                    throw new RuntimeException("Bind [" + service + "] already exists.");
                }

                if (instances.ContainsKey(service))
                {
                    throw new RuntimeException("Instances [" + service + "] is already exists.");
                }

                if (aliases.ContainsKey(service))
                {
                    throw new RuntimeException("Aliase [" + service + "] is already exists.");
                }

                var bindData = new BindData(this, service, concrete, isStatic);
                binds.Add(service, bindData);

                return bindData;
            }
        }

        /// <summary>
        /// 以依赖注入形式调用一个方法
        /// </summary>
        /// <param name="instance">方法对象</param>
        /// <param name="methodInfo">方法信息</param>
        /// <param name="userParams">用户传入的参数</param>
        /// <returns>方法返回值</returns>
        /// <exception cref="ArgumentNullException"><paramref name="instance"/>,<paramref name="methodInfo"/>为<c>null</c></exception>
        public object Call(object instance, MethodInfo methodInfo, params object[] userParams)
        {
            Guard.NotNull(instance, "instance");
            Guard.NotNull(methodInfo, "methodInfo");

            var type = instance.GetType();
            var parameter = new List<ParameterInfo>(methodInfo.GetParameters());

            lock (syncRoot)
            {
                var bindData = GetBindFillable(Type2Service(type));
                userParams = parameter.Count > 0 ? GetDependencies(bindData, parameter, userParams) : new object[] { };
                return methodInfo.Invoke(instance, userParams);
            }
        }

        /// <summary>
        /// 构造服务
        /// </summary>
        /// <param name="service">服务名或别名</param>
        /// <param name="userParams">用户传入的构造参数</param>
        /// <returns>服务实例，如果构造失败那么返回null</returns>
        /// <exception cref="ArgumentNullException"><paramref name="service"/>为<c>null</c>或者空字符串</exception>
        /// <exception cref="RuntimeException">出现循环依赖</exception>
        /// <returns>服务实例，如果构造失败那么返回null</returns>
        public object MakeWith(string service, params object[] userParams)
        {
            Guard.NotEmptyOrNull(service, "service");
            lock (syncRoot)
            {
                service = NormalizeService(service);
                service = AliasToService(service);

                object instance;
                if (instances.TryGetValue(service, out instance))
                {
                    return instance;
                }

                if (buildStack.Contains(service))
                {
                    throw new RuntimeException("Circular dependency detected while for [" + service + "].");
                }

                buildStack.Push(service);
                userParamsStack.Push(userParams);
                try
                {
                    return Resolve(service, null, true, userParams);
                }
                finally
                {
                    userParamsStack.Pop();
                    buildStack.Pop();
                }
            }
        }

        /// <summary>
        /// 构造服务
        /// </summary>
        /// <param name="service">服务名或别名</param>
        /// <exception cref="ArgumentNullException"><paramref name="service"/>为<c>null</c>或者空字符串</exception>
        /// <exception cref="RuntimeException">出现循环依赖</exception>
        /// <returns>服务实例，如果构造失败那么返回null</returns>
        public object Make(string service)
        {
            return MakeWith(service);
        }

        /// <summary>
        /// 构造服务
        /// </summary>
        /// <param name="service">服务名或者别名</param>
        /// <returns>服务实例，如果构造失败那么返回null</returns>
        public object this[string service]
        {
            get { return Make(service); }
        }

        /// <summary>
        /// 获取一个回调，当执行回调可以生成指定的服务
        /// </summary>
        /// <param name="service">服务名或别名</param>
        /// <returns>回调方案</returns>
        public Func<object> Factory(string service)
        {
            return () => Make(service);
        }

        /// <summary>
        /// 静态化一个服务,实例值会经过解决修饰器
        /// </summary>
        /// <param name="service">服务名或别名</param>
        /// <param name="instance">服务实例，<c>null</c>也是合法的实例值</param>
        /// <exception cref="ArgumentNullException"><paramref name="service"/>为<c>null</c>或者空字符串</exception>
        /// <exception cref="RuntimeException"><paramref name="service"/>的服务在绑定设置中不是静态的</exception>
        public void Instance(string service, object instance)
        {
            Guard.NotEmptyOrNull(service, "service");
            lock (syncRoot)
            {
                service = NormalizeService(service);
                service = AliasToService(service);

                var bindData = GetBind(service);
                if (bindData != null)
                {
                    if (!bindData.IsStatic)
                    {
                        throw new RuntimeException("Service [" + service + "] is not Singleton(Static) Bind.");
                    }
                    instance = ((BindData)bindData).TriggerResolving(instance);
                }
                else
                {
                    bindData = MakeEmptyBindData(service);
                }

                var isResolved = IsResolved(service);

                Release(service);

                instance = TriggerOnResolving(bindData, instance);
                instances.Add(service, instance);

                if (isResolved)
                {
                    TriggerOnRebound(service);
                }
            }
        }

        /// <summary>
        /// 释放静态化实例
        /// </summary>
        /// <param name="service">服务名或别名</param>
        public void Release(string service)
        {
            Guard.NotEmptyOrNull(service, "service");
            lock (syncRoot)
            {
                service = NormalizeService(service);
                service = AliasToService(service);

                object instance;
                if (!instances.TryGetValue(service, out instance))
                {
                    return;
                }

                var bindData = GetBindFillable(service);
                bindData.TriggerRelease(instance);
                TriggerOnRelease(bindData, instance);
                instances.Remove(service);
            }
        }

        /// <summary>
        /// 清空容器的所有实例，绑定，别名，标签，解决器
        /// </summary>
        public void Flush()
        {
            lock (syncRoot)
            {
                foreach (var service in Dict.Keys(instances))
                {
                    Release(service);
                }

                tags.Clear();
                aliases.Clear();
                aliasesReverse.Clear();
                instances.Clear();
                binds.Clear();
                resolving.Clear();
                release.Clear();
                resolved.Clear();
                findType.Clear();
                findTypeCache.Clear();
                buildStack.Clear();
                userParamsStack.Clear();
                rebound.Clear();
            }
        }

        /// <summary>
        /// 当查找类型无法找到时会尝试去调用开发者提供的查找类型函数
        /// </summary>
        /// <param name="finder">查找类型的回调</param>
        /// <param name="priority">查询优先级(值越小越优先)</param>
        /// <returns>当前容器实例</returns>
        public IContainer OnFindType(Func<string, Type> finder, int priority = int.MaxValue)
        {
            Guard.NotNull(finder, "finder");
            lock (syncRoot)
            {
                findType.Add(finder, priority);
            }
            return this;
        }

        /// <summary>
        /// 当静态服务被释放时
        /// </summary>
        /// <param name="callback">处理释放时的回调</param>
        /// <returns>当前容器实例</returns>
        public IContainer OnRelease(Action<IBindData, object> callback)
        {
            Guard.NotNull(callback, "callback");
            lock (syncRoot)
            {
                release.Add(callback);
            }
            return this;
        }

        /// <summary>
        /// 当服务被解决时，生成的服务会经过注册的回调函数
        /// </summary>
        /// <param name="callback">回调函数</param>
        /// <returns>当前容器对象</returns>
        public IContainer OnResolving(Func<IBindData, object, object> callback)
        {
            Guard.NotNull(callback, "callback");
            lock (syncRoot)
            {
                resolving.Add(callback);

                var result = new Dictionary<string, object>();
                foreach (var data in instances)
                {
                    var bindData = GetBindFillable(data.Key);
                    result[data.Key] = callback.Invoke(bindData, data.Value);
                }
                foreach (var data in result)
                {
                    instances[data.Key] = data.Value;
                }
                result.Clear();
            }
            return this;
        }

        /// <summary>
        /// 当一个已经被解决的服务，发生重定义时触发
        /// </summary>
        /// <param name="service">服务名</param>
        /// <param name="callback">回调</param>
        /// <returns>服务容器</returns>
        public IContainer OnRebound(string service, Action<IContainer, object> callback)
        {
            Guard.NotNull(callback, "callback");
            lock (syncRoot)
            {
                service = NormalizeService(service);
                service = AliasToService(service);

                List<Action<IContainer, object>> list;
                if (!rebound.TryGetValue(service, out list))
                {
                    rebound[service] = list = new List<Action<IContainer, object>>();
                }
                list.Add(callback);
            }
            return this;
        }

        /// <summary>
        /// 将类型转为服务名
        /// </summary>
        /// <param name="type">类型</param>
        /// <returns>服务名</returns>
        public string Type2Service(Type type)
        {
            return type.ToString();
        }

        /// <summary>
        /// 解除绑定服务
        /// </summary>
        /// <param name="service">服务名或者别名</param>
        public void UnBind(string service)
        {
            lock (syncRoot)
            {
                service = NormalizeService(service);
                service = AliasToService(service);

                Release(service);
                List<string> serviceList;
                if (aliasesReverse.TryGetValue(service, out serviceList))
                {
                    foreach (var alias in serviceList)
                    {
                        aliases.Remove(alias);
                    }
                    aliasesReverse.Remove(service);
                }
                binds.Remove(service);
            }
        }

        /// <summary>
        /// 从用户传入的参数中获取依赖
        /// </summary>
        /// <param name="baseParam">基础参数</param>
        /// <param name="userParams">用户传入参数</param>
        /// <returns>合适的注入参数</returns>
        protected virtual object GetDependenciesFromUserParams(ParameterInfo baseParam, ref object[] userParams)
        {
            if (userParams == null)
            {
                return null;
            }

            GuardUserParamsCount(userParams.Length);

            for (var n = 0; n < userParams.Length; n++)
            {
                var userParam = userParams[n];
                if (baseParam.ParameterType.IsInstanceOfType(userParam))
                {
                    return Arr.RemoveAt(ref userParams, n);
                }

                try
                {
                    if (baseParam.ParameterType.IsPrimitive && userParam is IConvertible)
                    {
                        var result = Convert.ChangeType(userParam, baseParam.ParameterType);
                        Arr.RemoveAt(ref userParams, n);
                        return result;
                    }
                }
                catch (Exception)
                {
                }
            }

            return null;
        }

        /// <summary>
        /// 获取字段需求服务
        /// </summary>
        /// <param name="property">字段</param>
        /// <returns>需求的服务名</returns>
        protected virtual string GetPropertyNeedsService(PropertyInfo property)
        {
            var injectAttr = (InjectAttribute)property.GetCustomAttributes(injectTarget, false)[0];
            return string.IsNullOrEmpty(injectAttr.Alias)
                ? Type2Service(property.PropertyType)
                : injectAttr.Alias;
        }

        /// <summary>
        /// 获取参数需求服务
        /// </summary>
        /// <param name="baseParam">当前正在解决的变量</param>
        /// <returns>需求的服务名</returns>
        protected virtual string GetParamNeedsService(ParameterInfo baseParam)
        {
            var needService = Type2Service(baseParam.ParameterType);
            if (baseParam.IsDefined(injectTarget, false))
            {
                var propertyAttrs = baseParam.GetCustomAttributes(injectTarget, false);
                if (propertyAttrs.Length > 0)
                {
                    var injectAttr = (InjectAttribute)propertyAttrs[0];
                    if (!string.IsNullOrEmpty(injectAttr.Alias))
                    {
                        needService = injectAttr.Alias;
                    }
                }
            }
            return needService;
        }

        /// <summary>
        /// 解决基本类型
        /// </summary>
        /// <param name="makeServiceBindData">请求注入操作的服务绑定数据</param>
        /// <param name="service">希望解决的服务名或者别名</param>
        /// <param name="baseParam">当前正在解决的变量</param>
        /// <returns>解决结果</returns>
        protected virtual object ResolveAttrPrimitive<T>(Bindable<T> makeServiceBindData, string service, PropertyInfo baseParam) where T : class, IBindable<T>
        {
            service = makeServiceBindData.GetContextual(service);
            if (CanMake(service))
            {
                return Make(service);
            }

            var result = SpeculationServiceByParamName(makeServiceBindData, baseParam.Name);
            if(result != null)
            {
                return result;
            }

            throw MakeUnresolvablePrimitiveException(baseParam.Name, baseParam.DeclaringType);
        }

        /// <summary>
        /// 解决类类型
        /// </summary>
        /// <param name="makeServiceBindData">请求注入操作的服务绑定数据</param>
        /// <param name="service">希望解决的服务名或者别名</param>
        /// <param name="baseParam">当前正在解决的变量</param>
        /// <returns>解决结果</returns>
        protected virtual object ResloveAttrClass<T>(Bindable<T> makeServiceBindData, string service, PropertyInfo baseParam) where T : class, IBindable<T>
        {
            return Make(makeServiceBindData.GetContextual(service));
        }

        /// <summary>
        /// 解决基本类型
        /// </summary>
        /// <param name="makeServiceBindData">请求注入操作的服务绑定数据</param>
        /// <param name="service">希望解决的服务名或者别名</param>
        /// <param name="baseParam">当前正在解决的变量</param>
        /// <returns>解决结果</returns>
        protected virtual object ResolvePrimitive<T>(Bindable<T> makeServiceBindData, string service , ParameterInfo baseParam) where T : class, IBindable<T>
        {
            service = makeServiceBindData.GetContextual(service);
            if (CanMake(service))
            {
                return Make(service);
            }

            var result = SpeculationServiceByParamName(makeServiceBindData, baseParam.Name);
            if(result != null)
            {
                return result;
            }

            if (baseParam.IsOptional)
            {
                return baseParam.DefaultValue;
            }

            throw MakeUnresolvablePrimitiveException(baseParam.Name, baseParam.Member.DeclaringType);
        }

        /// <summary>
        /// 解决类类型
        /// </summary>
        /// <param name="makeServiceBindData">请求注入操作的服务绑定数据</param>
        /// <param name="service">希望解决的服务名或者别名</param>
        /// <param name="baseParam">当前正在解决的变量</param>
        /// <returns>解决结果</returns>
        protected virtual object ResloveClass<T>(Bindable<T> makeServiceBindData, string service, ParameterInfo baseParam) where T : class, IBindable<T>
        {
            try
            {
                return Make(makeServiceBindData.GetContextual(service));
            }
            catch (UnresolvableException ex)
            {
                if (baseParam.IsOptional)
                {
                    return baseParam.DefaultValue;
                }
                throw ex;
            }
        }

        /// <summary>
        /// 根据参数名字来推测服务
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="makeServiceBindData">请求注入操作的服务绑定数据</param>
        /// <param name="paramName">参数名</param>
        /// <returns>推测的服务</returns>
        protected virtual object SpeculationServiceByParamName<T>(Bindable<T> makeServiceBindData, string paramName) where T : class, IBindable<T>
        {
            var service = makeServiceBindData.GetContextual("@" + paramName);
            return CanMake(service) ? Make(service) : null;
        }

        /// <summary>
        /// 触发全局解决修饰器
        /// </summary>
        /// <param name="bindData">服务绑定数据</param>
        /// <param name="obj">服务实例</param>
        /// <returns>被修饰器修饰后的服务实例</returns>
        protected virtual object TriggerOnResolving(IBindData bindData, object obj)
        {
            foreach (var func in resolving)
            {
                obj = func(bindData, obj);
            }
            return obj;
        }

        /// <summary>
        /// 触发全局释放修饰器
        /// </summary>
        /// <param name="bindData">服务绑定数据</param>
        /// <param name="obj">服务实例</param>
        /// <returns>被修饰器修饰后的服务实例</returns>
        protected virtual void TriggerOnRelease(IBindData bindData, object obj)
        {
            foreach (var action in release)
            {
                action.Invoke(bindData, obj);
            }
        }

        /// <summary>
        /// 触发服务重定义事件
        /// </summary>
        /// <param name="service">发生重定义的服务</param>
        protected virtual void TriggerOnRebound(string service)
        {
            var instance = Make(service);
            foreach (var callback in GetOnReboundCallbacks(service))
            {
                callback(this, instance);
            }
        }

        /// <summary>
        /// 获取重定义的服务所对应的回调
        /// </summary>
        /// <returns>回调列表</returns>
        protected virtual IEnumerable<Action<IContainer, object>> GetOnReboundCallbacks(string service)
        {
            List<Action<IContainer, object>> result;
            if (!rebound.TryGetValue(service, out result))
            {
                return new Action<IContainer, object>[] { };
            }
            return result;
        }

        /// <summary>
        /// 生成一个编译失败异常
        /// </summary>
        /// <param name="makeService">构造的服务名字</param>
        /// <param name="makeServiceType">构造的服务类型</param>
        /// <param name="innerException">内部异常</param>
        /// <returns>运行时异常</returns>
        protected virtual UnresolvableException MakeBuildFaildException(string makeService, Type makeServiceType, Exception innerException)
        {
            string message;
            if (makeServiceType != null)
            {
                message = "Target [" + makeServiceType + "] build faild. Service is [" + makeService + "]";
            }
            else
            {
                message = "Service [" + makeService + "] is not exists.";
            }
            
            if (buildStack.Count > 0)
            {
                var previous = string.Join(", ", buildStack.ToArray());
                message += " While building [" + previous + "].";
            }

            return new UnresolvableException(message, innerException);
        }

        /// <summary>
        /// 生成一个未能解决基本类型的异常
        /// </summary>
        /// <param name="name">变量名</param>
        /// <param name="declaringClass">变量所属类</param>
        /// <returns>运行时异常</returns>
        protected virtual UnresolvableException MakeUnresolvablePrimitiveException(string name, Type declaringClass)
        {
            var message = "Unresolvable primitive dependency , resolving [" + name + "] in class [" + declaringClass + "]";
            return new UnresolvableException(message);
        }

        /// <summary>
        /// 标准化服务名
        /// </summary>
        /// <param name="service">服务名</param>
        /// <returns>标准化后的服务名</returns>
        protected virtual string NormalizeService(string service)
        {
            return service.Trim();
        }

        /// <summary>
        /// 检查实例是否实现自某种类型
        /// </summary>
        /// <param name="type">需要实现自的类型</param>
        /// <param name="instance">生成的实例</param>
        /// <returns>是否符合类型</returns>
        protected virtual bool CheckInstanceIsInstanceOfType(Type type, object instance)
        {
            return instance == null || type.IsInstanceOfType(instance);
        }

        /// <summary>
        /// 保证用户传入参数必须小于指定值
        /// </summary>
        /// <param name="count">传入参数数量</param>
        protected virtual void GuardUserParamsCount(int count)
        {
            if (count > 255)
            {
                throw new RuntimeException("Too many parameters , must be less than 255");
            }
        }

        /// <summary>
        /// 守卫解决实例状态
        /// </summary>
        /// <param name="instance">服务实例</param>
        /// <param name="makeService">服务名</param>
        /// <param name="makeServiceType">服务类型</param>
        protected virtual void GuardResolveInstance(object instance,string makeService, Type makeServiceType)
        {
            if (instance == null)
            {
                throw MakeBuildFaildException(makeService, makeServiceType, null);
            }
        }

        /// <summary>
        /// 获取通过服务名获取服务的类型
        /// </summary>
        /// <param name="service">服务名</param>
        /// <returns>服务类型</returns>
        protected virtual Type GetServiceType(string service)
        {
            Type result;

            if (findTypeCache.TryGetValue(service, out result))
            {
                return result;
            }

            foreach (var finder in findType)
            {
                var type = finder.Invoke(service);
                if (type != null)
                {
                    return findTypeCache[service] = type;
                }
            }

            return findTypeCache[service] = null;
        }

        /// <summary>
        /// 属性注入
        /// </summary>
        /// <param name="makeServiceBindData">服务绑定数据</param>
        /// <param name="makeServiceInstance">服务实例</param>
        /// <returns>服务实例</returns>
        /// <exception cref="RuntimeException">属性是必须的或者注入类型和需求类型不一致</exception>
        protected void AttributeInject<T>(Bindable<T> makeServiceBindData, object makeServiceInstance) where T : class, IBindable<T>
        {
            if (makeServiceInstance == null)
            {
                return;
            }

            foreach (var property in makeServiceInstance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanWrite
                    || !property.IsDefined(injectTarget, false))
                {
                    continue;
                }

                var needService = GetPropertyNeedsService(property);

                object instance;
                if (property.PropertyType.IsClass
                    || property.PropertyType.IsInterface)
                {
                    instance = ResloveAttrClass(makeServiceBindData, needService, property);
                }
                else
                {
                    instance = ResolveAttrPrimitive(makeServiceBindData, needService, property);
                }

                if (!CheckInstanceIsInstanceOfType(property.PropertyType, instance))
                {
                    throw new UnresolvableException("[" + makeServiceBindData.Service + "] Attr inject type must be [" + property.PropertyType + "] , But instance is [" + instance.GetType() + "] , Make service is [" + needService + "].");
                }

                property.SetValue(makeServiceInstance, instance, null);
            }
        }

        /// <summary>
        /// 获取依赖解决结果
        /// </summary>
        /// <param name="makeServiceBindData">服务绑定数据</param>
        /// <param name="baseParams">服务实例的参数信息</param>
        /// <param name="userParams">输入的构造参数列表</param>
        /// <returns>服务所需参数的解决结果</returns>
        /// <exception cref="RuntimeException">生成的实例类型和需求类型不一致</exception>
        protected object[] GetDependencies<T>(Bindable<T> makeServiceBindData, IList<ParameterInfo> baseParams, object[] userParams) where T : class, IBindable<T>
        {
            var results = new List<object>();

            foreach (var baseParam in baseParams)
            {
                var instance = GetDependenciesFromUserParams(baseParam, ref userParams);
                if (instance != null)
                {
                    results.Add(instance);
                    continue;
                }

                var needService = GetParamNeedsService(baseParam);

                if (baseParam.ParameterType.IsClass
                    || baseParam.ParameterType.IsInterface)
                {
                    instance = ResloveClass(makeServiceBindData, needService, baseParam);
                }
                else
                {
                    instance = ResolvePrimitive(makeServiceBindData, needService, baseParam);
                }

                if (!CheckInstanceIsInstanceOfType(baseParam.ParameterType, instance))
                {
                    throw new UnresolvableException("[" + makeServiceBindData.Service + "] Params inject type must be [" + baseParam.ParameterType + "] , But instance is [" + instance.GetType() + "] Make service is [" + needService + "].");
                }

                results.Add(instance);
            }

            return results.ToArray();
        }

        /// <summary>
        /// 制作一个空的绑定数据
        /// </summary>
        /// <param name="service">服务名</param>
        /// <returns>空绑定数据</returns>
        protected BindData MakeEmptyBindData(string service)
        {
            return new BindData(this, service, null, false);
        }

        /// <summary>
        /// 解决服务
        /// </summary>
        /// <param name="makeService">服务名</param>
        /// <param name="makeServiceType">服务类型</param>
        /// <param name="isFromMake">是否直接调用自Make函数</param>
        /// <param name="userParams">用户传入的构造参数</param>
        /// <returns>服务实例</returns>
        private object Resolve(string makeService, Type makeServiceType, bool isFromMake, params object[] userParams)
        {
            var bindData = GetBindFillable(makeService);
            var buildInstance = isFromMake ? BuildUseConcrete(bindData, makeServiceType, userParams)
                : Build(bindData, makeServiceType ?? (makeServiceType = GetServiceType(bindData.Service)), userParams);

            GuardResolveInstance(buildInstance, makeService, makeServiceType);

            //只有是来自于make函数的调用时才执行di，包装，以及修饰
            if (!isFromMake)
            {
                return buildInstance;
            }

            AttributeInject(bindData, buildInstance);

            if (bindData.IsStatic)
            {
                Instance(makeService, buildInstance);
            }
            else
            {
                buildInstance = TriggerOnResolving(bindData, bindData.TriggerResolving(buildInstance));
            }

            resolved[makeService] = true;

            return buildInstance;
        }

        /// <summary>
        /// 构造服务实现
        /// </summary>
        /// <param name="makeServiceBindData">服务绑定数据</param>
        /// <param name="makeServiceType">服务类型</param>
        /// <param name="userParams">用户传入的构造参数</param>
        /// <returns>服务实例</returns>
        private object Build(BindData makeServiceBindData, Type makeServiceType, object[] userParams)
        {
            userParams = userParams ?? new object[] { };

            if (makeServiceType == null
                || makeServiceType.IsAbstract
                || makeServiceType.IsInterface)
            {
                return null;
            }

            var constructor = makeServiceType.GetConstructors();
            if (constructor.Length <= 0)
            {
                try
                {
                    return Activator.CreateInstance(makeServiceType);
                }
                catch (Exception ex)
                {
                    throw MakeBuildFaildException(makeServiceBindData.Service, makeServiceType, ex);
                }
            }

            var parameter = new List<ParameterInfo>(constructor[constructor.Length - 1].GetParameters());

            if (parameter.Count > 0)
            {
                userParams = GetDependencies(makeServiceBindData, parameter, userParams);
            }

            try
            {
                return Activator.CreateInstance(makeServiceType, userParams);
            }
            catch (Exception ex)
            {
                throw MakeBuildFaildException(makeServiceBindData.Service, makeServiceType, ex);
            }
        }

        /// <summary>
        /// 常规编译一个服务
        /// </summary>
        /// <param name="makeServiceBindData">服务绑定数据</param>
        /// <param name="makeServiceType">服务类型</param>
        /// <param name="param">构造参数</param>
        /// <returns>服务实例</returns>
        private object BuildUseConcrete(BindData makeServiceBindData, Type makeServiceType, object[] param)
        {
            return makeServiceBindData.Concrete != null ?
                makeServiceBindData.Concrete(this, param) :
                Resolve(makeServiceBindData.Service, makeServiceType, false, param);
        }

        /// <summary>
        /// 获取别名最终对应的服务名
        /// </summary>
        /// <param name="service">服务名或别名</param>
        /// <returns>最终映射的服务名</returns>
        private string AliasToService(string service)
        {
            string alias;
            return aliases.TryGetValue(service, out alias) ? alias : service;
        }

        /// <summary>
        /// 获取服务绑定数据,如果数据为null则填充数据
        /// </summary>
        /// <param name="service">服务名</param>
        /// <returns>服务绑定数据</returns>
        private BindData GetBindFillable(string service)
        {
            BindData bindData;
            return binds.TryGetValue(service, out bindData) ? bindData : MakeEmptyBindData(service);
        }
    }
}