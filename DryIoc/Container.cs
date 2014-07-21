﻿/*
The MIT License (MIT)

Copyright (c) 2013 Maksim Volkau

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;

namespace DryIoc
{
    /// <summary>
    /// IoC Container. Documentation is available at https://bitbucket.org/dadhi/dryioc.
    /// TODO:
    /// - add: CreateContainerWithWipedCache.
    /// - finish: CreateChildContainer and CreateScopedContainer.
    /// </summary>
    public class Container : IRegistry, IDisposable
    {
        public Container(ResolutionRules resolutionRules = null)
        {
            ResolutionRules = resolutionRules ?? ResolutionRules.Default;

            _factories = Ref.Of(HashTree<Type, object>.Empty);
            _decorators = Ref.Of(HashTree<Type, Factory[]>.Empty);
            _genericWrappers = Ref.Of(OpenGenericsSupport.GenericWrappers);

            _singletonScope = _currentScope = new Scope();

            _resolutionState = new ResolutionState();
            _resolvedDefaultDelegates = HashTree<Type, FactoryDelegate>.Empty;
            _resolvedKeyedDelegates = HashTree<Type, HashTree<object, FactoryDelegate>>.Empty;
        }

        /// <remarks>Use <paramref name="registerBatch"/> to register some services right after constructor creation. 
        /// For some who prefer lambda syntax.</remarks>
        public Container(Action<IRegistrator> registerBatch, ResolutionRules resolutionRules = null)
            : this(resolutionRules)
        {
            registerBatch.ThrowIfNull().Invoke(this);
        }

        public Container(ResolutionRules resolutionRules,
            Ref<HashTree<Type, object>> factories,
            Ref<HashTree<Type, Factory[]>> decorators,
            Ref<HashTree<Type, Factory>> genericWrappers,
            IScope singletonScope, IScope currentScope)
        {
            ResolutionRules = resolutionRules ?? ResolutionRules.Empty;

            _factories = factories;
            _decorators = decorators;
            _genericWrappers = genericWrappers;

            _singletonScope = singletonScope;
            _currentScope = currentScope;

            _resolutionState = new ResolutionState();
            _resolvedDefaultDelegates = HashTree<Type, FactoryDelegate>.Empty;
            _resolvedKeyedDelegates = HashTree<Type, HashTree<object, FactoryDelegate>>.Empty;
        }

        public Container WithNewRules(ResolutionRules newRules)
        {
            return new Container(newRules, _factories, _decorators, _genericWrappers, _singletonScope, _currentScope);
        }

        public Container OpenScope()
        {
            return new Container(ResolutionRules, _factories, _decorators, _genericWrappers, _singletonScope,
                new Scope());
        }

        public Container CreateChildContainer()
        {
            IRegistry parent = this;
            return new Container(ResolutionRules.With((request, _) =>
            {
                var factory = parent.ResolveFactory(request, IfUnresolved.ReturnNull);
                return factory == null ? null // unable to resolve from parent, proceed.
                    : new ExpressionFactory((childRequest, ignoredChild) => factory.GetExpression(childRequest, parent));
            }));
        }

        public Container WipeResolutionCache()
        {
            return new Container(ResolutionRules, _factories, _decorators, _genericWrappers, _singletonScope, _currentScope);
        }

        public void Dispose()
        {
            ((IDisposable)_currentScope).Dispose();
        }

        public Request CreateRequest(Type serviceType, object serviceKey = null)
        {
            return new Request(_resolutionState, Ref.Of<IScope>(), null, serviceType, serviceKey);
        }

        #region IRegistrator

        public void Register(Factory factory, Type serviceType, object serviceKey, IfAlreadyRegistered ifAlreadyRegistered)
        {
            factory.ThrowIfNull().VerifyBeforeRegistration(serviceType.ThrowIfNull(), this);
            switch (factory.Setup.Type)
            {
                case FactoryType.Decorator:
                    _decorators.Swap(x => x.AddOrUpdate(serviceType, new[] { factory }, ArrayTools.Append));
                    break;
                case FactoryType.GenericWrapper:
                    _genericWrappers.Swap(x => x.AddOrUpdate(serviceType, factory));
                    break;
                default:
                    AddOrUpdateFactory(factory, serviceType, serviceKey, ifAlreadyRegistered);
                    break;
            }
        }

        public bool IsRegistered(Type serviceType, object serviceKey, FactoryType factoryType, Func<Factory, bool> condition)
        {
            serviceType = serviceType.ThrowIfNull();
            switch (factoryType)
            {
                case FactoryType.GenericWrapper:
                    Throw.If(!serviceType.IsGenericType, Error.IS_REGISTERED_FOR_GENERIC_WRAPPER_CALLED_WITH_NONGENERIC_SERVICE_TYPE, serviceType);
                    var wrapper = _genericWrappers.Value.GetValueOrDefault(serviceType.GetGenericTypeDefinition());
                    return wrapper != null && (condition == null || condition(wrapper));

                case FactoryType.Decorator:
                    var decorators = _decorators.Value.GetValueOrDefault(serviceType);
                    return decorators != null && (condition == null || decorators.Any(condition));

                default:
                    return GetFactoryOrDefault(serviceType, serviceKey,
                        factories => factories.Select(x => x.Value).FirstOrDefault(condition ?? (factory => true)),
                        retryForOpenGenericServiceType: true) != null;
            }
        }

        public void Unregister(Type serviceType, object serviceKey, FactoryType factoryType, Func<Factory, bool> condition)
        {
            switch (factoryType)
            {
                case FactoryType.GenericWrapper:
                    if (condition == null)
                        _genericWrappers.Swap(_ => _.RemoveOrUpdate(serviceType));
                    else
                        _genericWrappers.Swap(_ => _.RemoveOrUpdate(serviceType,
                            (Factory oldFactory, out Factory newFactory) =>
                            {
                                newFactory = oldFactory;
                                return !condition(oldFactory);
                            }));
                    break;
                case FactoryType.Decorator:
                    if (condition == null)
                        _decorators.Swap(_ => _.RemoveOrUpdate(serviceType));
                    else
                        _decorators.Swap(_ => _.RemoveOrUpdate(serviceType, (Factory[] oldFactories, out Factory[] newFactories) =>
                        {
                            newFactories = oldFactories.Where(factory => !condition(factory)).ToArray();
                            return newFactories.Length != 0;
                        }));
                    break;
                default:
                    if (serviceKey == null && condition == null)
                        _factories.Swap(_ => _.RemoveOrUpdate(serviceType));
                    else
                        _factories.Swap(_ => _.RemoveOrUpdate(serviceType, (object oldEntry, out object newEntry) =>
                        {
                            newEntry = oldEntry; // by default hold old entry

                            if (oldEntry is Factory) // return false to remove entry
                                return serviceKey != null && !DefaultKey.Default.Equals(serviceKey) ||
                                       condition != null && !condition((Factory)oldEntry);

                            var factoriesEntry = (FactoriesEntry)oldEntry;
                            var oldFactories = factoriesEntry.Factories;
                            var newFactories = oldFactories;
                            if (serviceKey == null)
                            {   // remove all factories for which condition is true
                                foreach (var factory in newFactories.Enumerate())
                                    if (condition == null || condition(factory.Value))
                                        newFactories = newFactories.RemoveOrUpdate(factory.Key);
                            }
                            else
                            {   // remove factory with specified key if its found and condition is true
                                var factory = newFactories.GetValueOrDefault(serviceKey);
                                if (factory != null && (condition == null || condition(factory)))
                                    newFactories = newFactories.RemoveOrUpdate(serviceKey);
                            }

                            if (newFactories != oldFactories) // if we deleted something then make a cleanup
                            {
                                if (newFactories.IsEmpty)
                                    return false; // if no more remaining factories, then delete the whole entry

                                if (newFactories.Height == 1 && newFactories.Key.Equals(DefaultKey.Default))
                                    newEntry = newFactories.Value; // replace entry with single remaining default factory
                                else
                                {   // update last default key if current default key was removed
                                    var newDefaultKey = factoriesEntry.LastDefaultKey;
                                    if (newDefaultKey != null && newFactories.GetValueOrDefault(newDefaultKey) == null)
                                        newDefaultKey = newFactories.Enumerate().Select(x => x.Key).OfType<DefaultKey>()
                                            .OrderByDescending(key => key.RegistrationOrder).FirstOrDefault();
                                    newEntry = new FactoriesEntry(newDefaultKey, newFactories);
                                }
                            }

                            return true;
                        }));
                    break;
            }
        }

        #endregion

        #region IResolver

        object IResolver.ResolveDefault(Type serviceType, IfUnresolved ifUnresolved)
        {
            var factoryDelegate = _resolvedDefaultDelegates.GetValueOrDefault(serviceType);
            return factoryDelegate != null ? factoryDelegate(_resolutionState.Items, null)
                : ResolveAndCacheDefaultDelegate(serviceType, ifUnresolved);
        }

        private object ResolveAndCacheDefaultDelegate(Type serviceType, IfUnresolved ifUnresolved)
        {
            var request = CreateRequest(serviceType);
            var factory = ((IRegistry)this).ResolveFactory(request, ifUnresolved);
            if (factory == null)
                return null;

            var factoryDelegate = factory.GetDelegate(request, this);
            Interlocked.Exchange(ref _resolvedDefaultDelegates,
                _resolvedDefaultDelegates.AddOrUpdate(serviceType, factoryDelegate));
            return factoryDelegate(request.ResolutionState.Items, request.ResolutionScope);
        }

        object IResolver.ResolveKeyed(Type serviceType, object serviceKey, IfUnresolved ifUnresolved)
        {
            var factoryDelegates = _resolvedKeyedDelegates.GetValueOrDefault(serviceType);
            if (factoryDelegates != null)
            {
                var factoryDelegate = factoryDelegates.GetValueOrDefault(serviceKey);
                if (factoryDelegate != null)
                    return factoryDelegate(_resolutionState.Items, null);
            }

            var request = CreateRequest(serviceType, serviceKey);
            var factory = ((IRegistry)this).ResolveFactory(request, ifUnresolved);
            if (factory == null)
                return null;

            var newFactoryDelegate = factory.GetDelegate(request, this);
            Interlocked.Exchange(ref _resolvedKeyedDelegates,
                _resolvedKeyedDelegates.AddOrUpdate(serviceType,
                (factoryDelegates ?? HashTree<object, FactoryDelegate>.Empty).AddOrUpdate(serviceKey, newFactoryDelegate)));
            return newFactoryDelegate(request.ResolutionState.Items, request.ResolutionScope);
        }

        #endregion

        #region IRegistry

        RegistryWeakRef IRegistry.SelfWeakRef
        {
            get { return _selfWeakRef ?? (_selfWeakRef = new RegistryWeakRef(this)); }
        }

        public ResolutionRules ResolutionRules { get; private set; }

        IScope IRegistry.SingletonScope { get { return _singletonScope; } }
        IScope IRegistry.CurrentScope { get { return _currentScope; } }

        Factory IRegistry.ResolveFactory(Request request, IfUnresolved ifUnresolved)
        {
            var rules = ResolutionRules;
            var factory = GetFactoryOrDefault(request.ServiceType, request.ServiceKey, rules.FactorySelector);
            if (factory != null && factory.ProvidesFactoryForRequest)
                factory = factory.GetFactoryForRequestOrDefault(request, this);

            if (factory != null)
                return factory;

            var ruleFactory = rules.ForUnregisteredService.GetFirstNonDefault(r => r(request, this));
            if (ruleFactory != null)
            {
                Register(ruleFactory, request.ServiceType, request.ServiceKey, IfAlreadyRegistered.ThrowIfDuplicateKey);
                return ruleFactory;
            }

            Throw.If(ifUnresolved == IfUnresolved.Throw, Error.UNABLE_TO_RESOLVE_SERVICE, request);
            return null;
        }

        Factory IRegistry.GetFactoryOrDefault(Type serviceType, object serviceKey)
        {
            return GetFactoryOrDefault(serviceType.ThrowIfNull(), serviceKey, ResolutionRules.FactorySelector,
                retryForOpenGenericServiceType: true);
        }

        IEnumerable<KV<object, Factory>> IRegistry.GetAllFactories(Type serviceType)
        {
            var entry = _factories.Value.GetValueOrDefault(serviceType);
            if (entry == null && serviceType.IsGenericType && !serviceType.IsGenericTypeDefinition)
                entry = _factories.Value.GetValueOrDefault(serviceType.GetGenericTypeDefinition());

            return entry == null ? Enumerable.Empty<KV<object, Factory>>()
                : entry is Factory ? new[] { new KV<object, Factory>(DefaultKey.Default, (Factory)entry) }
                : ((FactoriesEntry)entry).Factories.Enumerate();
        }

        Expression IRegistry.GetDecoratorExpressionOrDefault(Request request)
        {
            // Stop if no decorators registered.
            var decorators = _decorators.Value;
            if (decorators.IsEmpty)
                return null;

            // Decorators for non service types are not supported.
            if (request.ResolvedFactory.Setup.Type != FactoryType.Service)
                return null;

            // We are already resolving decorator for the service, so stop now.
            var parent = request.GetNonWrapperParentOrDefault();
            if (parent != null && parent.ResolvedFactory.Setup.Type == FactoryType.Decorator)
                return null;

            var serviceType = request.ServiceType;

            // First look for decorators registered as Func of decorated service returning decorator - Func<TService, TService>.
            var decoratorFuncType = typeof(Func<,>).MakeGenericType(serviceType, serviceType);
            LambdaExpression resultFuncDecorator = null;
            var funcDecorators = decorators.GetValueOrDefault(decoratorFuncType);
            if (funcDecorators != null)
            {
                for (var i = 0; i < funcDecorators.Length; i++)
                {
                    var decorator = funcDecorators[i];
                    var decoratorRequest = request.ResolveWith(decorator, decoratorFuncType);
                    if (((DecoratorSetup)decorator.Setup).IsApplicable(request))
                    {
                        var newDecorator = decorator.GetExpression(decoratorRequest, this);
                        if (resultFuncDecorator == null)
                        {
                            var decorated = Expression.Parameter(serviceType, "decorated");
                            resultFuncDecorator = Expression.Lambda(Expression.Invoke(newDecorator, decorated), decorated);
                        }
                        else
                        {
                            var decorateDecorator = Expression.Invoke(newDecorator, resultFuncDecorator.Body);
                            resultFuncDecorator = Expression.Lambda(decorateDecorator, resultFuncDecorator.Parameters[0]);
                        }
                    }
                }
            }

            // Next look for normal decorators.
            var serviceDecorators = decorators.GetValueOrDefault(serviceType);
            var openGenericDecoratorIndex = serviceDecorators == null ? 0 : serviceDecorators.Length;
            var openGenericServiceType = request.OpenGenericServiceType;
            if (openGenericServiceType != null)
                serviceDecorators = serviceDecorators.Append(decorators.GetValueOrDefault(openGenericServiceType));

            Expression resultDecorator = resultFuncDecorator;
            if (serviceDecorators != null)
            {
                for (var i = 0; i < serviceDecorators.Length; i++)
                {
                    var decorator = serviceDecorators[i];
                    var decoratorRequest = request.ResolveWith(decorator);
                    if (((DecoratorSetup)decorator.Setup).IsApplicable(request))
                    {
                        // Cache closed generic registration produced by open-generic decorator.
                        if (i >= openGenericDecoratorIndex && decorator.ProvidesFactoryForRequest)
                        {
                            decorator = decorator.GetFactoryForRequestOrDefault(request, this);
                            Register(decorator, serviceType, null, IfAlreadyRegistered.ThrowIfDuplicateKey);
                        }

                        var decoratorExpr = request.ResolutionState.GetCachedFactoryExpressionOrDefault(decorator.ID);
                        if (decoratorExpr == null)
                        {
                            IList<Type> unusedFunArgs;
                            var funcExpr = decorator
                                .GetFuncWithArgsOrDefault(decoratorFuncType, decoratorRequest, this, out unusedFunArgs)
                                .ThrowIfNull(Error.DECORATOR_FACTORY_SHOULD_SUPPORT_FUNC_RESOLUTION, decoratorFuncType);
                            decoratorExpr = unusedFunArgs != null ? funcExpr.Body : funcExpr;
                            request.ResolutionState.CacheFactoryExpression(decorator.ID, decoratorExpr);
                        }

                        if (resultDecorator == null || !(decoratorExpr is LambdaExpression))
                            resultDecorator = decoratorExpr;
                        else
                        {
                            if (!(resultDecorator is LambdaExpression))
                                resultDecorator = Expression.Invoke(decoratorExpr, resultDecorator);
                            else
                            {
                                var prevDecorators = ((LambdaExpression)resultDecorator);
                                var decorateDecorator = Expression.Invoke(decoratorExpr, prevDecorators.Body);
                                resultDecorator = Expression.Lambda(decorateDecorator, prevDecorators.Parameters[0]);
                            }
                        }
                    }
                }
            }

            return resultDecorator;
        }

        Factory IRegistry.GetGenericWrapperOrDefault(Type openGenericServiceType)
        {
            return _genericWrappers.Value.GetValueOrDefault(openGenericServiceType);
        }

        Type IRegistry.GetWrappedServiceTypeOrSelf(Type serviceType)
        {
            if (!serviceType.IsGenericType)
                return serviceType;

            var factory = _genericWrappers.Value.GetValueOrDefault(serviceType.GetGenericTypeDefinition());
            if (factory == null || factory.Setup.Type != FactoryType.GenericWrapper)
                return serviceType;

            var wrapperSetup = ((GenericWrapperSetup)factory.Setup);
            var wrappedType = wrapperSetup.GetWrappedServiceType(serviceType.GetGenericArguments());
            return ((IRegistry)this).GetWrappedServiceTypeOrSelf(wrappedType);
        }

        #endregion

        #region Factories Add/Get

        private sealed class FactoriesEntry
        {
            public readonly DefaultKey LastDefaultKey;
            public readonly HashTree<object, Factory> Factories;

            public FactoriesEntry(DefaultKey lastDefaultKey, HashTree<object, Factory> factories)
            {
                LastDefaultKey = lastDefaultKey;
                Factories = factories;
            }
        }

        private void AddOrUpdateFactory(Factory factory, Type serviceType, object serviceKey, IfAlreadyRegistered ifAlreadyRegistered)
        {
            if (serviceKey == null)
            {
                _factories.Swap(x => x.AddOrUpdate(serviceType, factory, (oldValue, _) =>
                {
                    if (oldValue is Factory) // adding new default to registered default
                    {
                        switch (ifAlreadyRegistered)
                        {
                            case IfAlreadyRegistered.KeepRegistered:
                                return oldValue;
                            case IfAlreadyRegistered.UpdateRegistered:
                                return factory;
                            default:
                                return new FactoriesEntry(DefaultKey.Default.Next(), HashTree<object, Factory>.Empty
                                    .AddOrUpdate(DefaultKey.Default, (Factory)oldValue)
                                    .AddOrUpdate(DefaultKey.Default.Next(), factory));
                        }
                    }

                    // otherwise, when already have some keyed factories registered.
                    var oldEntry = ((FactoriesEntry)oldValue);
                    if (oldEntry.LastDefaultKey == null) // there was not default registration, add the first one.
                        return new FactoriesEntry(DefaultKey.Default, oldEntry.Factories.AddOrUpdate(DefaultKey.Default, factory));

                    switch (ifAlreadyRegistered)
                    {
                        case IfAlreadyRegistered.KeepRegistered:
                            return oldValue;
                        case IfAlreadyRegistered.UpdateRegistered:
                            return new FactoriesEntry(oldEntry.LastDefaultKey, oldEntry.Factories.Update(oldEntry.LastDefaultKey, factory));
                        default: // just add another default factory
                            var newDefaultKey = oldEntry.LastDefaultKey.Next();
                            return new FactoriesEntry(newDefaultKey, oldEntry.Factories.AddOrUpdate(newDefaultKey, factory));
                    }
                }));
            }
            else // for non default service key
            {
                var newEntry = new FactoriesEntry(null, HashTree<object, Factory>.Empty.AddOrUpdate(serviceKey, factory));

                _factories.Swap(x => x.AddOrUpdate(serviceType, newEntry, (oldValue, _) =>
                {
                    if (oldValue is Factory) // if registered is default, just add it to new entry
                        return new FactoriesEntry(DefaultKey.Default, newEntry.Factories.AddOrUpdate(DefaultKey.Default, (Factory)oldValue));

                    var oldEntry = ((FactoriesEntry)oldValue);
                    return new FactoriesEntry(oldEntry.LastDefaultKey, oldEntry.Factories.AddOrUpdate(serviceKey, factory, (oldFactory, __) =>
                    {
                        switch (ifAlreadyRegistered)
                        {
                            case IfAlreadyRegistered.KeepRegistered:
                                return oldFactory;
                            case IfAlreadyRegistered.UpdateRegistered:
                                return factory;
                            default:
                                throw Error.DUPLICATE_SERVICE_KEY.Of(serviceType, serviceKey, oldFactory);
                        }
                    }));
                }));
            }
        }

        private Factory GetFactoryOrDefault(Type serviceType, object serviceKey,
            ResolutionRules.FactorySelectorRule factorySelector,
            bool retryForOpenGenericServiceType = false)
        {
            var entry = _factories.Value.GetValueOrDefault(serviceType);
            if (entry == null && retryForOpenGenericServiceType &&
                serviceType.IsGenericType && !serviceType.IsGenericTypeDefinition)
                entry = _factories.Value.GetValueOrDefault(serviceType.GetGenericTypeDefinition());

            if (entry != null)
            {
                if (entry is Factory)
                {
                    if (serviceKey != null && !DefaultKey.Default.Equals(serviceKey))
                        return null;

                    var factory = (Factory)entry;
                    if (factorySelector != null)
                        return factorySelector(new[] { new KeyValuePair<object, Factory>(DefaultKey.Default, factory) });

                    return factory;
                }

                var factories = ((FactoriesEntry)entry).Factories;
                if (serviceKey != null)
                {
                    var factory = factories.GetValueOrDefault(serviceKey);
                    if (factorySelector != null)
                        return factorySelector(new[] { new KeyValuePair<object, Factory>(serviceKey, factory) });

                    return factory;
                }

                var defaultFactories = factories.Enumerate().Where(x => x.Key is DefaultKey).ToArray();
                if (defaultFactories.Length != 0)
                {
                    if (factorySelector != null)
                        return factorySelector(defaultFactories.Select(kv => new KeyValuePair<object, Factory>(kv.Key, kv.Value)));

                    if (defaultFactories.Length == 1)
                        return defaultFactories[0].Value;

                    if (defaultFactories.Length > 1)
                        throw Error.EXPECTED_SINGLE_DEFAULT_FACTORY.Of(serviceType, defaultFactories);
                }
            }

            return null;
        }

        #endregion

        #region Internal State

        private RegistryWeakRef _selfWeakRef;

        private readonly Ref<HashTree<Type, object>> _factories; // where object is Factory or KeyedFactoriesEntry
        private readonly Ref<HashTree<Type, Factory[]>> _decorators;
        private readonly Ref<HashTree<Type, Factory>> _genericWrappers;

        private readonly IScope _singletonScope, _currentScope;

        private HashTree<Type, FactoryDelegate> _resolvedDefaultDelegates;
        private HashTree<Type, HashTree<object, FactoryDelegate>> _resolvedKeyedDelegates;
        private readonly ResolutionState _resolutionState;

        #endregion
    }

    public sealed class DefaultKey
    {
        public static readonly DefaultKey Default = new DefaultKey(0);

        public DefaultKey Next()
        {
            return Of(RegistrationOrder + 1);
        }

        public readonly int RegistrationOrder;

        public override bool Equals(object other)
        {
            return other is DefaultKey && ((DefaultKey)other).RegistrationOrder == RegistrationOrder;
        }

        public override int GetHashCode()
        {
            return RegistrationOrder;
        }

        public override string ToString()
        {
            return "DefaultKey#" + RegistrationOrder;
        }

        #region Implementation

        private static DefaultKey[] _keyPool = { Default };

        private DefaultKey(int registrationOrder)
        {
            RegistrationOrder = registrationOrder;
        }

        private static DefaultKey Of(int registrationOrder)
        {
            if (registrationOrder < _keyPool.Length)
                return _keyPool[registrationOrder];

            var nextKey = new DefaultKey(registrationOrder);
            if (registrationOrder == _keyPool.Length)
                _keyPool = _keyPool.AppendOrUpdate(nextKey);
            return nextKey;
        }

        #endregion
    }

    public sealed class ResolutionState
    {
        public static readonly ParameterExpression ItemsParamExpr = Expression.Parameter(typeof(AppendableArray<object>), "items");

        public AppendableArray<object> Items
        {
            get { return _items; }
        }

        public int GetOrAddItem(object item)
        {
            var index = -1;
            Ref.Swap(ref _items, x =>
            {
                index = x.IndexOf(item);
                if (index == -1)
                    index = (x = x.Append(item)).Length - 1;
                return x;
            });
            return index;
        }

        public int GetOrAddItem(IRegistry registry)
        {
            return _registryWeakRefID != -1 ? _registryWeakRefID
                : (_registryWeakRefID = GetOrAddItem(registry.SelfWeakRef));
        }

        public Expression GetItemExpression(int itemIndex, Type itemType)
        {
            var itemExpr = _itemsExpressions.GetFirstValueByHashOrDefault(itemIndex);
            if (itemExpr == null)
            {
                var indexExpr = Expression.Constant(itemIndex, typeof(int));
                itemExpr = Expression.Convert(Expression.Call(ItemsParamExpr, _getItemMethod, indexExpr), itemType);
                Interlocked.Exchange(ref _itemsExpressions, _itemsExpressions.AddOrUpdate(itemIndex, itemExpr));
            }
            return itemExpr;
        }

        public Expression GetItemExpression(object item, Type itemType)
        {
            return GetItemExpression(GetOrAddItem(item), itemType);
        }

        public Expression GetItemExpression<T>(T item)
        {
            return GetItemExpression(GetOrAddItem(item), typeof(T));
        }

        public Expression GetItemExpression(IRegistry registry)
        {
            return Expression.Property(GetItemExpression(GetOrAddItem(registry), typeof(RegistryWeakRef)), "Target");
        }

        public Expression GetCachedFactoryExpressionOrDefault(int factoryID)
        {
            return _factoryExpressions.GetFirstValueByHashOrDefault(factoryID);
        }

        public void CacheFactoryExpression(int factoryID, Expression factoryExpression)
        {
            Interlocked.Exchange(ref _factoryExpressions, _factoryExpressions.AddOrUpdate(factoryID, factoryExpression));
        }

        #region Implementation

        private static readonly MethodInfo _getItemMethod = typeof(AppendableArray<object>).GetMethod("Get");

        private AppendableArray<object> _items = AppendableArray<object>.Empty;
        private HashTree<int, Expression> _itemsExpressions = HashTree<int, Expression>.Empty;
        private HashTree<int, Expression> _factoryExpressions = HashTree<int, Expression>.Empty;
        private int _registryWeakRefID = -1;

        #endregion
    }

    public sealed class RegistryWeakRef
    {
        public RegistryWeakRef(IRegistry registry)
        {
            _weakRef = new WeakReference(registry);
        }

        public IRegistry Target
        {
            get { return (_weakRef.Target as IRegistry).ThrowIfNull(Error.CONTAINER_IS_GARBAGE_COLLECTED); }
        }

        private readonly WeakReference _weakRef;
    }

    public sealed class AppendableArray<T>
    {
        public static readonly AppendableArray<T> Empty = new AppendableArray<T>();

        public readonly int Length;

        public AppendableArray<T> Append(T value)
        {
            return new AppendableArray<T>(Length + 1,
                _tree.AddOrUpdate(Length >> NODE_ARRAY_BIT_COUNT, new[] { value }, ArrayTools.Append));
        }

        public int IndexOf(T value)
        {
            foreach (var node in _tree.Enumerate())
            {
                var indexInNode = node.Value.IndexOf(x => ReferenceEquals(x, value) || Equals(x, value));
                if (indexInNode != -1)
                    return node.Key << NODE_ARRAY_BIT_COUNT | indexInNode;
            }

            return -1;
        }

        /// <remarks>Method relies on underlying array for index range checking.</remarks>
        public T Get(int index)
        {
            return _treeHasSingleNode ? _tree.Value[index]
                : _tree.GetFirstValueByHashOrDefault(index >> NODE_ARRAY_BIT_COUNT)[index & NODE_ARRAY_BIT_MASK];
        }

        #region Implementation

        // Node array length is number of items stored per tree node. 
        // When the item added to same node, array will be copied. So if array is too long performance will degrade.
        // Should be power of two: e.g. 2, 4, 8, 16, 32...
        internal const int NODE_ARRAY_LENGTH = 32;

        private const int NODE_ARRAY_BIT_MASK = NODE_ARRAY_LENGTH - 1; // for length 32 will be 11111 binary.
        private const int NODE_ARRAY_BIT_COUNT = 5;                    // number of set bits in NODE_ARRAY_BIT_MASK.

        private readonly HashTree<int, T[]> _tree;
        private readonly bool _treeHasSingleNode;

        private AppendableArray() : this(0, HashTree<int, T[]>.Empty) { }

        private AppendableArray(int length, HashTree<int, T[]> tree)
        {
            Length = length;
            _tree = tree;
            _treeHasSingleNode = length <= NODE_ARRAY_LENGTH;
        }

        #endregion
    }

    public delegate object FactoryDelegate(AppendableArray<object> items, IScope resolutionScope);

    public static partial class FactoryCompiler
    {
        public static Expression<FactoryDelegate> ToFactoryExpression(this Expression expression)
        {
            // Removing not required Convert from expression root, because CompiledFactory result still be converted at the end.
            if (expression.NodeType == ExpressionType.Convert)
                expression = ((UnaryExpression)expression).Operand;
            if (expression.Type.IsValueType)
                expression = Expression.Convert(expression, typeof(object));
            return Expression.Lambda<FactoryDelegate>(expression, ResolutionState.ItemsParamExpr, Request.ResolutionScopeParamExpr);
        }

        public static FactoryDelegate CompileToDelegate(this Expression expression, IRegistry registry)
        {
            var factoryExpression = expression.ToFactoryExpression();
            FactoryDelegate factoryDelegate = null;
            CompileToMethod(factoryExpression, registry, ref factoryDelegate);
            // ReSharper disable ConstantNullCoalescingCondition
            return factoryDelegate ?? factoryExpression.Compile();
            // ReSharper restore ConstantNullCoalescingCondition
        }

        // Partial method definition to be implemented in .NET40 version of Container.
        // It is optional and fine to be not implemented.
        static partial void CompileToMethod(Expression<FactoryDelegate> factoryExpression, IRegistry registry, ref FactoryDelegate result);
    }

    public static class OpenGenericsSupport
    {
        public static readonly Type[] FuncTypes = { typeof(Func<>), typeof(Func<,>), typeof(Func<,,>), typeof(Func<,,,>), typeof(Func<,,,,>) };
        public static readonly HashTree<Type, Factory> GenericWrappers;

        static OpenGenericsSupport()
        {
            GenericWrappers = HashTree<Type, Factory>.Empty;
            GenericWrappers = GenericWrappers.AddOrUpdate(typeof(Many<>),
                new FactoryProvider(
                    (_, __) => new ExpressionFactory(GetManyExpression),
                    GenericWrapperSetup.Default));

            var funcFactory = new FactoryProvider(
                (_, __) => new ExpressionFactory(GetFuncExpression),
                GenericWrapperSetup.With(t => t[t.Length - 1]));
            foreach (var funcType in FuncTypes)
                GenericWrappers = GenericWrappers.AddOrUpdate(funcType, funcFactory);

            GenericWrappers = GenericWrappers.AddOrUpdate(typeof(Lazy<>),
                new ReflectionFactory(typeof(Lazy<>),
                    constructorSelector: (t, req, reg) => t.GetConstructor(new[] { typeof(Func<>).MakeGenericType(t.GetGenericArguments()) }),
                    setup: GenericWrapperSetup.Default));

            GenericWrappers = GenericWrappers.AddOrUpdate(typeof(KeyValuePair<,>),
                new FactoryProvider(GetKeyValuePairFactoryOrDefault, GenericWrapperSetup.With(t => t[1])));

            GenericWrappers = GenericWrappers.AddOrUpdate(typeof(Meta<,>),
                new FactoryProvider(GetMetaFactoryOrDefault, GenericWrapperSetup.With(t => t[0])));

            GenericWrappers = GenericWrappers.AddOrUpdate(typeof(DebugExpression<>),
                new FactoryProvider((_, __) => new ExpressionFactory(GetDebugExpression), GenericWrapperSetup.Default));
        }

        public static readonly ResolutionRules.ResolveUnregisteredServiceRule ResolveOpenGenerics = (request, registry) =>
        {
            var openGenericServiceType = request.OpenGenericServiceType;
            if (openGenericServiceType == null)
                return null;

            var factory = registry.GetFactoryOrDefault(openGenericServiceType, request.ServiceKey)
                ?? registry.GetGenericWrapperOrDefault(openGenericServiceType);

            if (factory != null && factory.ProvidesFactoryForRequest)
                factory = factory.GetFactoryForRequestOrDefault(request, registry);

            return factory;
        };

        public static readonly ResolutionRules.ResolveUnregisteredServiceRule ResolveEnumerableOrArray = (request, registry) =>
        {
            if (!request.ServiceType.IsArray && request.OpenGenericServiceType != typeof(IEnumerable<>))
                return null;

            return new ExpressionFactory(
                setup: GenericWrapperSetup.Default,
                expressionFactory: (req, reg) =>
                {
                    var collectionType = req.ServiceType;

                    var itemType = collectionType.IsArray
                        ? collectionType.GetElementType()
                        : collectionType.GetGenericArguments()[0];

                    var wrappedItemType = reg.GetWrappedServiceTypeOrSelf(itemType);

                    // Composite pattern support: filter out composite root from available keys.
                    var items = reg.GetAllFactories(wrappedItemType);
                    var parent = req.GetNonWrapperParentOrDefault();
                    if (parent != null && parent.ServiceType == wrappedItemType)
                    {
                        var parentFactoryID = parent.ResolvedFactory.ID;
                        items = items.Where(x => x.Value.ID != parentFactoryID);
                    }

                    var itemArray = items.ToArray();
                    Throw.If(itemArray.Length == 0, Error.UNABLE_TO_FIND_REGISTERED_ENUMERABLE_ITEMS, wrappedItemType, req);

                    var itemExpressions = new List<Expression>(itemArray.Length);
                    for (var i = 0; i < itemArray.Length; i++)
                    {
                        var item = itemArray[i];
                        var itemRequest = req.Push(itemType, item.Key);
                        var itemFactory = reg.ResolveFactory(itemRequest, IfUnresolved.ReturnNull);
                        if (itemFactory != null)
                            itemExpressions.Add(itemFactory.GetExpression(itemRequest, registry));
                    }

                    Throw.If(itemExpressions.Count == 0, Error.UNABLE_TO_RESOLVE_ENUMERABLE_ITEMS, itemType, req);
                    var newArrayExpr = Expression.NewArrayInit(itemType.ThrowIfNull(), itemExpressions);
                    return newArrayExpr;
                });
        };

        public static Expression GetManyExpression(Request request, IRegistry registry)
        {
            var dynamicEnumerableType = request.ServiceType;
            var itemType = dynamicEnumerableType.GetGenericArguments()[0];

            var wrappedItemType = registry.GetWrappedServiceTypeOrSelf(itemType);

            // Composite pattern support: filter out composite root from available keys.
            var parentFactoryID = 0;
            var parent = request.GetNonWrapperParentOrDefault();
            if (parent != null && parent.ServiceType == wrappedItemType)
                parentFactoryID = parent.ResolvedFactory.ID;

            var resolveMethod = _resolveManyDynamicallyMethod.MakeGenericMethod(itemType, wrappedItemType);

            var registryRefExpr = request.ResolutionState.GetItemExpression(registry.SelfWeakRef);
            var resolveCallExpr = Expression.Call(resolveMethod, registryRefExpr, Expression.Constant(parentFactoryID));

            return Expression.New(dynamicEnumerableType.GetConstructors()[0], resolveCallExpr);
        }

        public static Expression GetFuncExpression(Request request, IRegistry registry)
        {
            var funcType = request.ServiceType;
            var funcTypeArgs = funcType.GetGenericArguments();
            var serviceType = funcTypeArgs[funcTypeArgs.Length - 1];

            var serviceRequest = request.Push(serviceType, request.ServiceKey);
            var serviceFactory = registry.ResolveFactory(serviceRequest, IfUnresolved.Throw);

            if (funcTypeArgs.Length == 1)
                return Expression.Lambda(funcType, serviceFactory.GetExpression(serviceRequest, registry), null);

            IList<Type> unusedFuncArgs;
            var funcExpr = serviceFactory.GetFuncWithArgsOrDefault(funcType, serviceRequest, registry, out unusedFuncArgs)
                .ThrowIfNull(Error.UNSUPPORTED_FUNC_WITH_ARGS, funcType, serviceRequest)
                .ThrowIf(unusedFuncArgs != null, Error.SOME_FUNC_PARAMS_ARE_UNUSED, unusedFuncArgs, request);
            return funcExpr;
        }

        public static Expression GetDebugExpression(Request request, IRegistry registry)
        {
            var ctor = request.ServiceType.GetConstructors()[0];
            var serviceType = request.ServiceType.GetGenericArguments()[0];
            var serviceRequest = request.Push(serviceType, request.ServiceKey);
            var factory = registry.ResolveFactory(serviceRequest, IfUnresolved.Throw);
            var factoryExpr = factory.GetExpression(serviceRequest, registry).ToFactoryExpression();
            return Expression.New(ctor, request.ResolutionState.GetItemExpression(factoryExpr));
        }

        public static Factory GetKeyValuePairFactoryOrDefault(Request request, IRegistry registry)
        {
            var typeArgs = request.ServiceType.GetGenericArguments();
            var serviceKeyType = typeArgs[0];
            var serviceKey = request.ServiceKey;
            if (serviceKey == null && serviceKeyType.IsValueType ||
                serviceKey != null && !serviceKeyType.IsInstanceOfType(serviceKey))
                return null;

            var serviceType = typeArgs[1];
            return new ExpressionFactory((pairReq, _) =>
            {
                var serviceRequest = pairReq.Push(serviceType, serviceKey);
                var serviceExpr = registry.ResolveFactory(serviceRequest, IfUnresolved.Throw).GetExpression(serviceRequest, registry);
                var pairCtor = pairReq.ServiceType.GetConstructors()[0];
                var keyExpr = pairReq.ResolutionState.GetItemExpression(serviceKey, serviceKeyType);
                var pairExpr = Expression.New(pairCtor, keyExpr, serviceExpr);
                return pairExpr;
            });
        }

        public static Factory GetMetaFactoryOrDefault(Request request, IRegistry registry)
        {
            var typeArgs = request.ServiceType.GetGenericArguments();
            var serviceType = typeArgs[0];
            var metadataType = typeArgs[1];

            var wrappedServiceType = registry.GetWrappedServiceTypeOrSelf(serviceType);
            object resultMetadata = null;
            var serviceKey = request.ServiceKey;
            if (serviceKey == null)
            {
                var result = registry.GetAllFactories(wrappedServiceType).FirstOrDefault(kv =>
                    kv.Value.Setup.Metadata != null && metadataType.IsInstanceOfType(kv.Value.Setup.Metadata));
                if (result != null)
                {
                    serviceKey = result.Key;
                    resultMetadata = result.Value.Setup.Metadata;
                }
            }
            else
            {
                var factory = registry.GetFactoryOrDefault(wrappedServiceType, serviceKey);
                if (factory != null)
                {
                    var metadata = factory.Setup.Metadata;
                    resultMetadata = metadata != null && metadataType.IsInstanceOfType(metadata) ? metadata : null;
                }
            }

            if (resultMetadata == null)
                return null;

            return new ExpressionFactory((req, _) =>
            {
                var serviceRequest = req.Push(serviceType, serviceKey);
                var serviceExpr = registry.ResolveFactory(serviceRequest, IfUnresolved.Throw).GetExpression(serviceRequest, registry);
                var metaCtor = req.ServiceType.GetConstructors()[0];
                var metadataExpr = req.ResolutionState.GetItemExpression(resultMetadata, metadataType);
                var metaExpr = Expression.New(metaCtor, serviceExpr, metadataExpr);
                return metaExpr;
            });
        }

        #region Tools

        public static bool IsFunc(this Request request)
        {
            return request != null && request.OpenGenericServiceType != null
                && FuncTypes.Contains(request.OpenGenericServiceType);
        }

        public static bool IsFuncWithArgs(this Request request)
        {
            return request.IsFunc() && request.OpenGenericServiceType != typeof(Func<>);
        }

        #endregion

        #region Implementation

        private static readonly MethodInfo _resolveManyDynamicallyMethod =
            typeof(OpenGenericsSupport).GetMethod("ResolveManyDynamically", BindingFlags.Static | BindingFlags.NonPublic);

        internal static IEnumerable<TService> ResolveManyDynamically<TService, TWrappedService>(
            RegistryWeakRef registryWeakRef, int parentFactoryID)
        {
            var itemType = typeof(TService);
            var wrappedItemType = typeof(TWrappedService);
            var registry = registryWeakRef.Target;

            var items = registry.GetAllFactories(wrappedItemType);
            if (parentFactoryID != -1)
                items = items.Where(kv => kv.Value.ID != parentFactoryID);

            foreach (var item in items)
            {
                var service = registry.ResolveKeyed(itemType, item.Key, IfUnresolved.ReturnNull);
                if (service != null) // skip unresolved items
                    yield return (TService)service;
            }
        }

        #endregion
    }

    public sealed partial class ResolutionRules
    {
        public static readonly ResolutionRules Empty = new ResolutionRules();

        public static ResolutionRules Default = Empty.With(
            OpenGenericsSupport.ResolveOpenGenerics,
            OpenGenericsSupport.ResolveEnumerableOrArray);

        public delegate Factory FactorySelectorRule(IEnumerable<KeyValuePair<object, Factory>> factories);
        public FactorySelectorRule FactorySelector { get; private set; }
        public ResolutionRules WithFactorySelector(FactorySelectorRule rule)
        {
            return new ResolutionRules(this) { FactorySelector = rule };
        }

        public ConstructorSelector ConstructorSelector { get; private set; }
        public ResolutionRules WithConstructorSelector(ConstructorSelector rule)
        {
            return new ResolutionRules(this) { ConstructorSelector = rule };
        }

        public delegate Factory ResolveUnregisteredServiceRule(Request request, IRegistry registry);
        public ResolveUnregisteredServiceRule[] ForUnregisteredService { get; private set; }
        public ResolutionRules With(params ResolveUnregisteredServiceRule[] rules)
        {
            return new ResolutionRules(this) { ForUnregisteredService = rules };
        }

        public delegate object ResolveConstructorParameterServiceKeyRule(ParameterInfo parameter, Request parent, IRegistry registry);
        public ResolveConstructorParameterServiceKeyRule[] ForConstructorParameterServiceKey { get; private set; }
        public ResolutionRules With(params ResolveConstructorParameterServiceKeyRule[] rules)
        {
            return new ResolutionRules(this) { ForConstructorParameterServiceKey = rules };
        }

        public static readonly BindingFlags PropertyOrFieldFlags = BindingFlags.Public | BindingFlags.Instance;

        public delegate bool ResolvePropertyOrFieldWithServiceKeyRule(out object key, MemberInfo member, Request parent, IRegistry registry);
        public ResolvePropertyOrFieldWithServiceKeyRule[] ForPropertyOrFieldWithServiceKey { get; private set; }

        public ResolutionRules With(params ResolvePropertyOrFieldWithServiceKeyRule[] rules)
        {
            return new ResolutionRules(this) { ForPropertyOrFieldWithServiceKey = rules };
        }

        #region Implementation

        private ResolutionRules() { }

        private ResolutionRules(ResolutionRules rules)
        {
            FactorySelector = rules.FactorySelector;
            ConstructorSelector = rules.ConstructorSelector;
            ForUnregisteredService = rules.ForUnregisteredService;
            ForConstructorParameterServiceKey = rules.ForConstructorParameterServiceKey;
            ForPropertyOrFieldWithServiceKey = rules.ForPropertyOrFieldWithServiceKey;

            _compilationToDynamicAssemblyEnabled = rules._compilationToDynamicAssemblyEnabled;
        }

        private bool _compilationToDynamicAssemblyEnabled; // used by .NET 4 and higher versions.

        #endregion
    }

    public static class Error
    {
        public static readonly string UNABLE_TO_RESOLVE_SERVICE =
            "Unable to resolve {0}." + Environment.NewLine +
            "Please register service OR adjust container resolution rules.";

        public static readonly string UNSUPPORTED_FUNC_WITH_ARGS =
            "Unsupported resolution as {0} of {1}.";

        public static readonly string EXPECTED_IMPL_TYPE_ASSIGNABLE_TO_SERVICE_TYPE =
            "Expecting implementation type {0} to be assignable to service type {1} but it is not.";

        public static readonly string UNABLE_TO_REGISTER_NON_FACTORY_PROVIDER_FOR_OPEN_GENERIC_SERVICE =
            "Unable to register not a factory provider for open-generic service {0}.";

        public static readonly string UNABLE_TO_REGISTER_OPEN_GENERIC_IMPL_WITH_NON_GENERIC_SERVICE =
            "Unable to register open-generic implementation {0} with non-generic service {1}.";

        public static readonly string UNABLE_TO_REGISTER_OPEN_GENERIC_IMPL_CAUSE_SERVICE_DOES_NOT_SPECIFY_ALL_TYPE_ARGS =
            "Unable to register open-generic implementation {0} because service {1} should specify all of its type arguments, but specifies only {2}.";

        public static readonly string USUPPORTED_REGISTRATION_OF_NON_GENERIC_IMPL_TYPE_DEFINITION_BUT_WITH_GENERIC_ARGS =
            "Unsupported registration of implementation {0} which is not a generic type definition but contains generic parameters." + Environment.NewLine +
            "Consider to register generic type definition {1} instead.";

        public static readonly string USUPPORTED_REGISTRATION_OF_NON_GENERIC_SERVICE_TYPE_DEFINITION_BUT_WITH_GENERIC_ARGS =
            "Unsupported registration of service {0} which is not a generic type definition but contains generic parameters." + Environment.NewLine +
            "Consider to register generic type definition {1} instead.";

        public static readonly string EXPECTED_SINGLE_DEFAULT_FACTORY =
            "Expecting single default registration of {0} but found many:" + Environment.NewLine + "{1}." + Environment.NewLine +
            "Please identify service with keys or metadata OR adjust resolution rules to select single registered factory.";

        public static readonly string EXPECTED_NON_ABSTRACT_IMPL_TYPE =
            "Expecting not abstract and not interface implementation type, but found {0}.";

        public static readonly string NO_PUBLIC_CONSTRUCTOR_DEFINED =
            "There is no public constructor defined for {0}.";

        public static readonly string UNSPECIFIED_HOWTO_SELECT_CONSTRUCTOR_FOR_IMPLTYPE =
            "Unspecified how to select single constructor for implementation type {0} with {1} public constructors.";

        public static readonly string CONSTRUCTOR_MISSES_SOME_PARAMETERS =
            "Constructor [{0}] of {1} misses some arguments required for {2} dependency.";

        public static readonly string UNABLE_TO_SELECT_CONSTRUCTOR =
            "Unable to select single constructor from {0} available in {1}." + Environment.NewLine +
            "Please provide constructor selector when registering service.";

        public static readonly string EXPECTED_FUNC_WITH_MULTIPLE_ARGS =
            "Expecting Func with one or more arguments but found {0}.";

        public static readonly string EXPECTED_CLOSED_GENERIC_SERVICE_TYPE =
            "Expecting closed-generic service type but found {0}.";

        public static readonly string RECURSIVE_DEPENDENCY_DETECTED =
            "Recursive dependency is detected in resolution of:" + Environment.NewLine + "{0}.";

        public static readonly string SCOPE_IS_DISPOSED =
            "Scope is disposed and scoped instances are no longer available.";

        public static readonly string CONTAINER_IS_GARBAGE_COLLECTED =
            "Container is no longer available (has been garbage-collected).";

        public static readonly string DUPLICATE_SERVICE_KEY =
            "Service {0} with the same key '{1}' is already registered as {2}.";

        public static readonly string GENERIC_WRAPPER_EXPECTS_SINGLE_TYPE_ARG_BY_DEFAULT =
            "Generic Wrapper is working with single service type only, but found many:" + Environment.NewLine + "{0}." + Environment.NewLine +
            "Please specify service type selector in Generic Wrapper setup upon registration.";

        public static readonly string SOME_FUNC_PARAMS_ARE_UNUSED =
            "Found some unused Func parameters:" + Environment.NewLine + "{0}" + Environment.NewLine + "when resolving {1}.";

        public static readonly string DECORATOR_FACTORY_SHOULD_SUPPORT_FUNC_RESOLUTION =
            "Decorator factory should support resolution as {0}, but it does not.";

        public static readonly string UNABLE_TO_FIND_REGISTERED_ENUMERABLE_ITEMS =
            "Unable to find registered services of item type (unwrapped) {0} when resolving {1}.";

        public static readonly string UNABLE_TO_RESOLVE_ENUMERABLE_ITEMS =
            "Unable to resolve any service of item type {0} when resolving {1}.";

        public static readonly string DELEGATE_FACTORY_EXPRESSION_RETURNED_NULL =
            "Delegate factory expression returned NULL when resolving {0}.";

        public static readonly string UNABLE_TO_MATCH_IMPL_BASE_TYPES_WITH_SERVICE_TYPE =
            "Unable to match service with any of open-generic implementation {0} implemented types {1} when resolving {2}.";

        public static readonly string UNABLE_TO_FIND_OPEN_GENERIC_IMPL_TYPE_ARG_IN_SERVICE =
            "Unable to find for open-generic implementation {0} the type argument {1} when resolving {2}.";

        public static readonly string IS_REGISTERED_FOR_GENERIC_WRAPPER_CALLED_WITH_NONGENERIC_SERVICE_TYPE =
            "IsRegistered for GenericWrapper called with non generic service type {0}.";

        public static readonly string UNABLE_TO_SELECT_CTOR_USING_SELECTOR =
            "Unable to get constructor of {0} using provided constructor selector.";

        public static readonly string UNABLE_TO_FIND_CTOR_WITH_ALL_RESOLVABLE_ARGS =
            "Unable to find constructor with all resolvable parameters when resolving {0}.";

        public static readonly string UNABLE_TO_FIND_MATCHING_CTOR_FOR_FUNC_WITH_ARGS =
            "Unable to find constructor with all parameters matching Func signature {0} " + Environment.NewLine +
            "and the rest of parameters resolvable from Container when resolving: {1}.";

        public static readonly string REGISTERED_FACTORY_DELEGATE_RETURNS_OBJECT_NOT_ASSIGNABLE_TO_SERVICE_TYPE =
            "Registered factory delegate returns object [{0}] of type {1}, which is not assignable to serviceType {2}.";

        public static readonly string REGISTERED_INSTANCE_OBJECT_NOT_ASSIGNABLE_TO_SERVICE_TYPE =
            "Registered instance [{0}] of type {1} is not assignable to serviceType {2}.";
    }

    public static class Registrator
    {
        /// <summary>
        /// Registers service <paramref name="serviceType"/>.
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">The service type to register</param>
        /// <param name="factory"><see cref="Factory"/> details object.</param>
        /// <param name="named">Optional service key (name). Could be of any type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void Register(this IRegistrator registrator, Type serviceType, Factory factory,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers service of <typeparamref name="TService"/>.
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="factory"><see cref="Factory"/> details object.</param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void Register<TService>(this IRegistrator registrator, Factory factory,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            registrator.Register(factory, typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers service <paramref name="serviceType"/> with corresponding <paramref name="implementationType"/>.
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">The service type to register.</param>
        /// <param name="implementationType">Implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void Register(this IRegistrator registrator, Type serviceType,
            Type implementationType, IReuse reuse = null, ConstructorSelector withConstructor = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            var factory = new ReflectionFactory(implementationType, reuse, withConstructor, setup);
            registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers service of <paramref name="implementationType"/>. ServiceType will be the same as <paramref name="implementationType"/>.
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="implementationType">Implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void Register(this IRegistrator registrator,
            Type implementationType, IReuse reuse = null, ConstructorSelector withConstructor = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            var factory = new ReflectionFactory(implementationType, reuse, withConstructor, setup);
            registrator.Register(factory, implementationType, named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers service of <typeparamref name="TService"/> type implemented by <typeparamref name="TImplementation"/> type.
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <typeparam name="TImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void Register<TService, TImplementation>(this IRegistrator registrator,
            IReuse reuse = null, ConstructorSelector withConstructor = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
            where TImplementation : TService
        {
            var factory = new ReflectionFactory(typeof(TImplementation), reuse, withConstructor, setup);
            registrator.Register(factory, typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers implementation type <typeparamref name="TImplementation"/> with itself as service type.
        /// </summary>
        /// <typeparam name="TImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void Register<TImplementation>(this IRegistrator registrator,
            IReuse reuse = null, ConstructorSelector withConstructor = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            var factory = new ReflectionFactory(typeof(TImplementation), reuse, withConstructor, setup);
            registrator.Register(factory, typeof(TImplementation), named, ifAlreadyRegistered);
        }

        public static Func<Type, bool> RegisterAllDefaultTypes = t => (t.IsPublic || t.IsNestedPublic) && t != typeof(object);

        /// <summary>
        /// Registers single registration for all implemented public interfaces and base classes.
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="implementationType">Service implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="types">Optional condition to include selected types only. Default value is <see cref="RegisterAllDefaultTypes"/></param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterAll(this IRegistrator registrator,
            Type implementationType, IReuse reuse = null, ConstructorSelector withConstructor = null, FactorySetup setup = null,
            Func<Type, bool> types = null, object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            var registration = new ReflectionFactory(implementationType, reuse, withConstructor, setup);

            var implementedTypes = implementationType.GetImplementedTypes(TypeTools.IncludeItself.AsFirst);
            var implementedServiceTypes = implementedTypes.Where(types ?? RegisterAllDefaultTypes);
            if (implementationType.IsGenericTypeDefinition)
            {
                var implTypeArgs = implementationType.GetGenericArguments();
                implementedServiceTypes = implementedServiceTypes
                    .Where(t => t.IsGenericType && t.ContainsGenericParameters && t.ContainsAllGenericParameters(implTypeArgs))
                    .Select(t => t.GetGenericTypeDefinition());
            }

            var atLeastOneRegistered = false;
            foreach (var serviceType in implementedServiceTypes)
            {
                registrator.Register(registration, serviceType, named, ifAlreadyRegistered);
                atLeastOneRegistered = true;
            }

            Throw.If(!atLeastOneRegistered, "Unable to register any of implementation {0} implemented services {1}.",
                implementationType, implementedTypes);
        }

        /// <summary>
        /// Registers single registration for all implemented public interfaces and base classes.
        /// </summary>
        /// <typeparam name="TImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterAll<TImplementation>(this IRegistrator registrator,
            IReuse reuse = null, ConstructorSelector withConstructor = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            registrator.RegisterAll(typeof(TImplementation), reuse, withConstructor, setup, null, named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers a factory delegate for creating an instance of <typeparamref name="TService"/>.
        /// Delegate can use <see cref="IResolver"/> parameter to resolve any required dependencies, e.g.:
        /// <code>RegisterDelegate&lt;ICar&gt;(r => new Car(r.Resolve&lt;IEngine&gt;()))</code>
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="factoryDelegate">The delegate used to create a instance of <typeparamref name="TService"/>.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterDelegate<TService>(this IRegistrator registrator,
            Func<IResolver, TService> factoryDelegate, IReuse reuse = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            var factory = new DelegateFactory(r => factoryDelegate(r), reuse, setup);
            registrator.Register(factory, typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers a factory delegate for creating an instance of <paramref name="serviceType"/>.
        /// Delegate can use <see cref="IResolver"/> parameter to resolve any required dependencies, e.g.:
        /// <code>RegisterDelegate&lt;ICar&gt;(r => new Car(r.Resolve&lt;IEngine&gt;()))</code>
        /// </summary>
        /// <param name="serviceType">Service type to register.</param>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="factoryDelegate">The delegate used to create a instance of <paramref name="serviceType"/>.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterDelegate(this IRegistrator registrator, Type serviceType,
            Func<IResolver, object> factoryDelegate, IReuse reuse = null, FactorySetup setup = null,
            object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            var factory = new DelegateFactory(r => factoryDelegate(r).ThrowIf(x => serviceType.IsInstanceOfType(x) ? null :
                Error.REGISTERED_FACTORY_DELEGATE_RETURNS_OBJECT_NOT_ASSIGNABLE_TO_SERVICE_TYPE.Of(x, x.GetType(), serviceType)),
                reuse, setup);
            registrator.Register(factory, serviceType, named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers a pre-created object of <typeparamref name="TService"/>.
        /// It is just a sugar on top of <see cref="RegisterDelegate{TService}"/> method.
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="instance">The pre-created instance of <typeparamref name="TService"/>.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterInstance<TService>(this IRegistrator registrator, TService instance,
            FactorySetup setup = null, object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            registrator.Register(new InstanceFactory(instance, setup), typeof(TService), named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Registers a pre-created object assignable to <paramref name="serviceType"/>. 
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">Service type to register.</param>
        /// <param name="instance">The pre-created instance of <paramref name="serviceType"/>.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="ifAlreadyRegistered">Optional policy to deal with case when service with such type and name is already registered.</param>
        public static void RegisterInstance(this IRegistrator registrator, Type serviceType, object instance,
            FactorySetup setup = null, object named = null, IfAlreadyRegistered ifAlreadyRegistered = IfAlreadyRegistered.ThrowIfDuplicateKey)
        {
            registrator.Register(new InstanceFactory(instance, setup), serviceType, named, ifAlreadyRegistered);
        }

        /// <summary>
        /// Returns true if <paramref name="serviceType"/> is registered in container or its open generic definition is registered in container.
        /// </summary>
        /// <param name="registrator">Usually <see cref="Container"/> to explore or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="serviceType">The type of the registered service.</param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="factoryType">Optional factory type to lookup, <see cref="FactoryType.Service"/> by default.</param>
        /// <param name="condition">Optional condition to specify what registered factory do you expect.</param>
        /// <returns>True if <paramref name="serviceType"/> is registered, false - otherwise.</returns>
        public static bool IsRegistered(this IRegistrator registrator, Type serviceType,
            object named = null, FactoryType factoryType = FactoryType.Service, Func<Factory, bool> condition = null)
        {
            return registrator.IsRegistered(serviceType, named, factoryType, condition);
        }

        /// <summary>
        /// Returns true if <typeparamref name="TService"/> type is registered in container or its open generic definition is registered in container.
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Usually <see cref="Container"/> to explore or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="factoryType">Optional factory type to lookup, <see cref="FactoryType.Service"/> by default.</param>
        /// <param name="condition">Optional condition to specify what registered factory do you expect.</param>
        /// <returns>True if <typeparamref name="TService"/> name="serviceType"/> is registered, false - otherwise.</returns>
        public static bool IsRegistered<TService>(this IRegistrator registrator,
            object named = null, FactoryType factoryType = FactoryType.Service, Func<Factory, bool> condition = null)
        {
            return registrator.IsRegistered(typeof(TService), named, factoryType, condition);
        }

        /// <summary> Removes specified registration from container.</summary>
        /// <param name="registrator">Usually <see cref="Container"/> to explore or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="serviceType">Type of service to remove.</param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="factoryType">Optional factory type to lookup, <see cref="FactoryType.Service"/> by default.</param>
        /// <param name="condition">Optional condition for Factory to be removed.</param>
        public static void Unregister(this IRegistrator registrator, Type serviceType,
            object named = null, FactoryType factoryType = FactoryType.Service, Func<Factory, bool> condition = null)
        {
            registrator.Unregister(serviceType, named, factoryType, condition);
        }

        /// <summary> Removes specified registration from container.</summary>
        /// <typeparam name="TService">The type of service to remove.</typeparam>
        /// <param name="registrator">Usually <see cref="Container"/> to explore or any other <see cref="IRegistrator"/> implementation.</param>
        /// <param name="named">Optional service key (name). Could be of any of type with overridden <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/>.</param>
        /// <param name="factoryType">Optional factory type to lookup, <see cref="FactoryType.Service"/> by default.</param>
        /// <param name="condition">Optional condition for Factory to be removed.</param>
        public static void Unregister<TService>(this IRegistrator registrator,
            object named = null, FactoryType factoryType = FactoryType.Service, Func<Factory, bool> condition = null)
        {
            registrator.Unregister(typeof(TService), named, factoryType, condition);
        }
    }

    public static class Resolver
    {
        /// <summary>
        /// Returns an instance of statically known <typepsaramref name="TService"/> type.
        /// </summary>
        /// <param name="serviceType">The type of the requested service.</param>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="ifUnresolved">Optional, say to how to handle unresolved service case.</param>
        /// <returns>The requested service instance.</returns>
        public static object Resolve(this IResolver resolver, Type serviceType, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return resolver.ResolveDefault(serviceType, ifUnresolved);
        }

        /// <summary>
        /// Returns an instance of statically known <typepsaramref name="TService"/> type.
        /// </summary>
        /// <typeparam name="TService">The type of the requested service.</typeparam>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="ifUnresolved">Optional, say to how to handle unresolved service case.</param>
        /// <returns>The requested service instance.</returns>
        public static TService Resolve<TService>(this IResolver resolver, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return (TService)resolver.ResolveDefault(typeof(TService), ifUnresolved);
        }

        /// <summary>
        /// Returns an instance of statically known <typepsaramref name="TService"/> type.
        /// </summary>
        /// <param name="serviceType">The type of the requested service.</param>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceKey">Service key (any type with <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/> defined).</param>
        /// <param name="ifUnresolved">Optional, say to how to handle unresolved service case.</param>
        /// <returns>The requested service instance.</returns>
        public static object Resolve(this IResolver resolver, Type serviceType, object serviceKey, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return serviceKey == null
                ? resolver.ResolveDefault(serviceType, ifUnresolved)
                : resolver.ResolveKeyed(serviceType, serviceKey, ifUnresolved);
        }

        /// <summary>
        /// Returns an instance of statically known <typepsaramref name="TService"/> type.
        /// </summary>
        /// <typeparam name="TService">The type of the requested service.</typeparam>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceKey">Service key (any type with <see cref="object.GetHashCode"/> and <see cref="object.Equals(object)"/> defined).</param>
        /// <param name="ifUnresolved">Optional, say to how to handle unresolved service case.</param>
        /// <returns>The requested service instance.</returns>
        public static TService Resolve<TService>(this IResolver resolver, object serviceKey, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return (TService)resolver.Resolve(typeof(TService), serviceKey, ifUnresolved);
        }

        /// <summary>
        /// For given instance resolves and sets non-initialized (null) properties from container.
        /// It does not throw if property is not resolved, so you might need to check property value afterwards.
        /// </summary>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="instance">Service instance with properties to resolve and initialize.</param>
        /// <param name="getServiceKey">Optional function to get service key, if not specified service key will be null.</param>
        public static void ResolvePropertiesAndFields(this IResolver resolver, object instance, Func<MemberInfo, object> getServiceKey = null)
        {
            var implType = instance.ThrowIfNull().GetType();
            getServiceKey = getServiceKey ?? (_ => null);

            foreach (var property in implType.GetProperties(ResolutionRules.PropertyOrFieldFlags).Where(p => p.GetSetMethod() != null))
            {
                var value = resolver.Resolve(property.PropertyType, getServiceKey(property), IfUnresolved.ReturnNull);
                if (value != null)
                    property.SetValue(instance, value, null);
            }

            foreach (var field in implType.GetFields(ResolutionRules.PropertyOrFieldFlags).Where(f => !f.IsInitOnly))
            {
                var value = resolver.Resolve(field.FieldType, getServiceKey(field), IfUnresolved.ReturnNull);
                if (value != null)
                    field.SetValue(instance, value);
            }
        }
    }

    public enum DependencyResolutionAction { Resolve, TryResolve, Skip };

    public sealed class DependencyResolutionInfo
    {
        public readonly DependencyResolutionAction Action;
        public readonly Type ServiceType;
        public readonly object ServiceKey;

        public static DependencyResolutionInfo Resolve(Type serviceType, object serviceKey = null)
        {
            return new DependencyResolutionInfo(DependencyResolutionAction.Resolve, serviceType, serviceKey);
        }

        public static DependencyResolutionInfo TryResolve(Type serviceType, object serviceKey = null)
        {
            return new DependencyResolutionInfo(DependencyResolutionAction.TryResolve, serviceType, serviceKey);
        }

        public static DependencyResolutionInfo Skip()
        {
            return _skipDependency;
        }

        private DependencyResolutionInfo(DependencyResolutionAction action, Type serviceType, object serviceKey)
        {
            Action = action;
            ServiceType = serviceType;
            ServiceKey = serviceKey;
        }

        private static readonly DependencyResolutionInfo _skipDependency = new DependencyResolutionInfo(DependencyResolutionAction.Skip, null, null);
    }

    public sealed class Request
    {
        #region ResolutionScope

        public static readonly ParameterExpression ResolutionScopeParamExpr = Expression.Parameter(typeof(IScope), "scope");

        public static IScope GetScope(ref IScope scope)
        {
            return scope = scope ?? new Scope();
        }

        public static readonly MethodInfo GetScopeMethod = typeof(Request).GetMethod("GetScope");
        public static readonly Expression ResolutionScopeExpr = Expression.Call(GetScopeMethod, ResolutionScopeParamExpr);

        public IScope ResolutionScope
        {
            get { return _resolutionScope.Value; }
        }

        public IScope CreateResolutionScope()
        {
            if (_resolutionScope.Value == null)
                _resolutionScope.Swap(scope => scope ?? new Scope());
            return _resolutionScope.Value;
        }

        #endregion

        ///<remarks>Reference to resolved items and cached factory expressions. 
        /// Used to propagate the state from resolution root, probably from another container (request creator).</remarks>
        public readonly ResolutionState ResolutionState;

        public readonly Request Parent;         // null for resolution root
        public readonly Type ServiceType;
        public readonly object ServiceKey;      // null by default, string for named or integer index for multiple defaults
        public readonly object DependencyInfo;  // either Reflection.ParameterInfo, PropertyInfo or FieldInfo. Used for Print only
        public readonly Factory ResolvedFactory;

        public Type OpenGenericServiceType
        {
            get { return ServiceType.IsGenericType ? ServiceType.GetGenericTypeDefinition() : null; }
        }

        public Type ImplementationType
        {
            get { return ResolvedFactory == null ? null : ResolvedFactory.ImplementationType; }
        }

        public Request Push(Type serviceType, object serviceKey, object dependencyInfo = null)
        {
            return new Request(ResolutionState, _resolutionScope, this, serviceType, serviceKey, dependencyInfo);
        }

        public Request ResolveWith(Factory factory, Type decoratorFuncType = null)
        {
            if (factory.Setup.Type == FactoryType.Service) // skip dependency recursion check for non-services (decorators, wrappers, ..)
                for (var p = Parent; p != null; p = p.Parent)
                    if (p.ResolvedFactory != null && p.ResolvedFactory.ID == factory.ID)
                        throw Error.RECURSIVE_DEPENDENCY_DETECTED.Of(this);
            var serviceType = decoratorFuncType ?? ServiceType;
            return new Request(ResolutionState, _resolutionScope, Parent, serviceType, ServiceKey, DependencyInfo, factory);
        }

        public Request Push(DependencyResolutionInfo resolutionInfo, object dependency)
        {
            return Push(resolutionInfo.ServiceType, resolutionInfo.ServiceKey, dependency);
        }

        public Request GetNonWrapperParentOrDefault()
        {
            var p = Parent;
            while (p != null && p.ResolvedFactory.Setup.Type == FactoryType.GenericWrapper)
                p = p.Parent;
            return p;
        }

        public IEnumerable<Request> Enumerate()
        {
            for (var x = this; x != null; x = x.Parent)
                yield return x;
        }

        /// <remarks>Pretty prints request as something like "DryIoc.UnitTests.IService 'blah' (ctorParam 'blah') of DryIoc.UnitTests.Service"</remarks>
        public string Print()
        {
            var str = new StringBuilder();

            if (ResolvedFactory != null && ResolvedFactory.Setup.Type != FactoryType.Service)
                str.Append(Enum.GetName(typeof(FactoryType), ResolvedFactory.Setup.Type)).Append(' ');

            str.Append(ServiceType.Print());

            if (ServiceKey != null)
                str.Append(" '").Append(ServiceKey).Append("'");

            if (DependencyInfo != null)
            {
                str.Append(" as ");
                if (DependencyInfo is ParameterInfo)
                    str.Append("ctor-parameter '").Append(((ParameterInfo)DependencyInfo).Name);
                else if (DependencyInfo is PropertyInfo)
                    str.Append("property '").Append(((PropertyInfo)DependencyInfo).Name);
                else if (DependencyInfo is FieldInfo)
                    str.Append("field '").Append(((FieldInfo)DependencyInfo).Name);
                str.Append("'");
            }

            if (ImplementationType != null && ImplementationType != ServiceType)
                str.Append(" of ").Append(ImplementationType.Print());

            return str.ToString();
        }

        public override string ToString()
        {
            var message = new StringBuilder().Append(Print());
            return Parent == null ? message.ToString()
                : Parent.Enumerate().Aggregate(message,
                    (m, r) => m.AppendLine().Append("in ").Append(r.Print())).ToString();
        }

        #region Implementation

        internal Request(ResolutionState resolutionState, Ref<IScope> resolutionScope,
            Request parent, Type serviceType, object serviceKey = null,
            object dependencyInfo = null, Factory factory = null)
        {
            ResolutionState = resolutionState;
            _resolutionScope = resolutionScope;
            Parent = parent;
            ServiceType = serviceType.ThrowIfNull()
                .ThrowIf(serviceType.IsGenericTypeDefinition, Error.EXPECTED_CLOSED_GENERIC_SERVICE_TYPE, serviceType);
            ServiceKey = serviceKey;
            DependencyInfo = dependencyInfo;
            ResolvedFactory = factory;
        }

        private readonly Ref<IScope> _resolutionScope;

        #endregion
    }

    public enum FactoryType { Service, Decorator, GenericWrapper };

    public enum FactoryCachePolicy { ShouldNotCacheExpression, CouldCacheExpression };

    public abstract class FactorySetup
    {
        public abstract FactoryType Type { get; }
        public virtual FactoryCachePolicy CachePolicy { get { return FactoryCachePolicy.ShouldNotCacheExpression; } }
        public virtual object Metadata { get { return null; } }
    }

    public class ServiceSetup : FactorySetup
    {
        public static readonly ServiceSetup Default = new ServiceSetup();

        public static ServiceSetup With(FactoryCachePolicy cachePolicy = FactoryCachePolicy.CouldCacheExpression, object metadata = null)
        {
            return cachePolicy == FactoryCachePolicy.CouldCacheExpression && metadata == null ? Default : new ServiceSetup(cachePolicy, metadata);
        }

        public static ServiceSetup WithMetadata(object metadata = null)
        {
            return metadata == null ? Default : new ServiceSetup(metadata: metadata);
        }

        public static ServiceSetup WithMetadata(Func<object> getMetadata)
        {
            return new ServiceSetup(getMetadata: getMetadata);
        }

        public override FactoryType Type { get { return FactoryType.Service; } }
        public override FactoryCachePolicy CachePolicy { get { return _cachePolicy; } }
        public override object Metadata
        {
            get { return _metadata ?? (_metadata = _getMetadata == null ? null : _getMetadata()); }
        }

        #region Implementation

        private ServiceSetup(
            FactoryCachePolicy cachePolicy = FactoryCachePolicy.CouldCacheExpression,
            object metadata = null, Func<object> getMetadata = null)
        {
            _cachePolicy = cachePolicy;
            _metadata = metadata;
            _getMetadata = getMetadata;
        }

        private readonly FactoryCachePolicy _cachePolicy;
        private readonly Func<object> _getMetadata;
        private object _metadata;

        #endregion
    }

    public class GenericWrapperSetup : FactorySetup
    {
        public static readonly GenericWrapperSetup Default = new GenericWrapperSetup();

        public static Func<Type[], Type> SelectServiceTypeArgDefault = ThrowIfNotSingleTypeArg;

        public static GenericWrapperSetup With(Func<Type[], Type> selectServiceTypeArg)
        {
            return selectServiceTypeArg == null ? Default : new GenericWrapperSetup(selectServiceTypeArg);
        }

        public override FactoryType Type { get { return FactoryType.GenericWrapper; } }
        public readonly Func<Type[], Type> GetWrappedServiceType;

        public static Type ThrowIfNotSingleTypeArg(Type[] typeArgs)
        {
            Throw.If(typeArgs.Length != 1, Error.GENERIC_WRAPPER_EXPECTS_SINGLE_TYPE_ARG_BY_DEFAULT, typeArgs);
            return typeArgs[0];
        }

        #region Implementation

        private GenericWrapperSetup(Func<Type[], Type> selectServiceTypeArg = null)
        {
            GetWrappedServiceType = selectServiceTypeArg ?? ThrowIfNotSingleTypeArg;
        }

        #endregion
    }

    public class DecoratorSetup : FactorySetup
    {
        public static readonly DecoratorSetup Default = new DecoratorSetup();

        public static DecoratorSetup With(Func<Request, bool> condition = null)
        {
            return condition == null ? Default : new DecoratorSetup(condition);
        }

        public override FactoryType Type { get { return FactoryType.Decorator; } }
        public readonly Func<Request, bool> IsApplicable;

        #region Implementation

        private DecoratorSetup(Func<Request, bool> condition = null)
        {
            IsApplicable = condition ?? (_ => true);
        }

        #endregion
    }

    public abstract class Factory
    {
        public static readonly FactorySetup DefaultSetup = ServiceSetup.Default;

        public readonly int ID;
        public readonly IReuse Reuse;

        public FactorySetup Setup
        {
            get { return _setup; }
            protected internal set { _setup = value ?? DefaultSetup; }
        }

        public virtual Type ImplementationType { get { return null; } }
        public virtual bool ProvidesFactoryForRequest { get { return false; } }

        protected Factory(IReuse reuse = null, FactorySetup setup = null)
        {
            ID = Interlocked.Increment(ref _idSeedAndCount);
            Reuse = reuse;
            Setup = setup;
        }

        public virtual void VerifyBeforeRegistration(Type serviceType, IRegistry registry)
        {
            if (serviceType.IsGenericTypeDefinition && !ProvidesFactoryForRequest)
                throw Error.UNABLE_TO_REGISTER_NON_FACTORY_PROVIDER_FOR_OPEN_GENERIC_SERVICE.Of(serviceType);
        }

        public virtual Factory GetFactoryForRequestOrDefault(Request request, IRegistry registry) { return null; }

        public abstract Expression CreateExpression(Request request, IRegistry registry);

        public virtual LambdaExpression CreateFuncWithArgsOrDefault(Type funcType, Request request, IRegistry registry, out IList<Type> unusedFuncArgs)
        {
            unusedFuncArgs = null;
            return null;
        }

        public virtual Expression GetExpression(Request request, IRegistry registry)
        {
            request = request.ResolveWith(this);

            var decorator = registry.GetDecoratorExpressionOrDefault(request);
            if (decorator != null && !(decorator is LambdaExpression))
                return decorator;

            Expression expression = null;

            if (Setup.CachePolicy == FactoryCachePolicy.CouldCacheExpression)
                expression = request.ResolutionState.GetCachedFactoryExpressionOrDefault(ID);

            if (expression == null)
            {
                expression = CreateExpression(request, registry);

                if (Reuse != null)
                {
                    var scope = Reuse.GetScope(request, registry);

                    // When singleton scope and no Func in request chain 
                    // then reused instance should can be inserted directly instead of calling Scope method.
                    if (scope == registry.SingletonScope &&
                        (request.Parent == null || !request.Parent.Enumerate().Any(OpenGenericsSupport.IsFunc)))
                    {
                        var factoryDelegate = expression.ToFactoryExpression().Compile();
                        var reusedInstance = scope.GetOrAdd(ID, () => factoryDelegate(request.ResolutionState.Items, request.ResolutionScope));
                        expression = request.ResolutionState.GetItemExpression(reusedInstance, expression.Type);
                    }
                    else
                    {
                        expression = GetReusedItemExpression(request, scope, expression);
                    }
                }

                if (Setup.CachePolicy == FactoryCachePolicy.CouldCacheExpression)
                    request.ResolutionState.CacheFactoryExpression(ID, expression);
            }

            if (decorator != null)
                expression = Expression.Invoke(decorator, expression);

            return expression;
        }

        public virtual LambdaExpression GetFuncWithArgsOrDefault(Type funcType, Request request, IRegistry registry, out IList<Type> unusedFuncArgs)
        {
            request = request.ResolveWith(this);
            var func = CreateFuncWithArgsOrDefault(funcType, request, registry, out unusedFuncArgs);
            if (func == null)
                return null;

            var decorator = registry.GetDecoratorExpressionOrDefault(request);
            if (decorator != null && !(decorator is LambdaExpression))
                return Expression.Lambda(funcType, decorator, func.Parameters);

            if (Reuse != null)
            {
                var scope = Reuse.GetScope(request, registry);
                var reusedInstanceExpr = GetReusedItemExpression(request, scope, func.Body);
                func = Expression.Lambda(funcType, reusedInstanceExpr, func.Parameters);
            }

            if (decorator != null)
                func = Expression.Lambda(funcType, Expression.Invoke(decorator, func.Body), func.Parameters);

            return func;
        }

        public virtual FactoryDelegate GetDelegate(Request request, IRegistry registry)
        {
            return GetExpression(request, registry).CompileToDelegate(registry);
        }

        public override string ToString()
        {
            var str = new StringBuilder();
            str.Append("factory {ID=").Append(ID);
            if (ImplementationType != null)
                str.Append(", ImplType=").Append(ImplementationType.Print());
            if (Reuse != null)
                str.Append(", ReuseType=").Append(Reuse.GetType().Print());
            if (Setup.Type != DefaultSetup.Type)
                str.Append(", FactoryType=").Append(Setup.Type);
            str.Append("}");
            return str.ToString();
        }

        #region Implementation

        private static int _idSeedAndCount;
        private FactorySetup _setup;

        private static readonly MethodInfo _scopeGetOrAddMethod = typeof(IScope).GetMethod("GetOrAdd");

        protected Expression GetReusedItemExpression(Request request, IScope scope, Expression expression)
        {
            var scopeExpr = scope == request.ResolutionScope ? Request.ResolutionScopeExpr
                : request.ResolutionState.GetItemExpression(scope);

            var getScopedItemMethod = _scopeGetOrAddMethod.MakeGenericMethod(expression.Type);

            var factoryIDExpr = Expression.Constant(ID);
            var factoryExpr = Expression.Lambda(expression, null);
            return Expression.Call(scopeExpr, getScopedItemMethod, factoryIDExpr, factoryExpr);
        }

        #endregion
    }

    public sealed class InstanceFactory : Factory
    {
        public InstanceFactory(object instance, FactorySetup setup = null)
            : base(null, setup)
        {
            _instance = instance;
        }

        public override void VerifyBeforeRegistration(Type serviceType, IRegistry _)
        {
            if (!serviceType.IsInstanceOfType(_instance))
                throw Error.REGISTERED_INSTANCE_OBJECT_NOT_ASSIGNABLE_TO_SERVICE_TYPE.Of(_instance, _instance.GetType(), serviceType);
        }

        public override Expression CreateExpression(Request request, IRegistry _)
        {
            return request.ResolutionState.GetItemExpression(_instance, _instance.GetType());
        }

        public override FactoryDelegate GetDelegate(Request _, IRegistry __)
        {
            return (i, s) => _instance;
        }

        private readonly object _instance;
    }

    public delegate ConstructorInfo ConstructorSelector(Type implementationType, Request request, IRegistry registry);

    public sealed class ReflectionFactory : Factory
    {
        #region Constructor selection strategies..

        public static ConstructorInfo SelectConstructorWithAllResolvableArguments(Type type, Request request, IRegistry registry)
        {
            var ctors = type.GetConstructors();
            if (ctors.Length == 0)
                return null; // Delegate handling of constructor absence to caller code.

            if (ctors.Length == 1)
                return ctors[0];

            var ctorsWithMoreParamsFirst = ctors
                .Select(c => new { Ctor = c, Params = c.GetParameters() })
                .OrderByDescending(x => x.Params.Length);

            if (request.Parent.IsFuncWithArgs())
            {
                // For Func with arguments, match constructor should contain all input arguments and the rest should be resolvable.
                var funcType = request.Parent.ServiceType;
                var funcArgs = funcType.GetGenericArguments();
                var inputArgCount = funcArgs.Length - 1;

                var matchedCtor = ctorsWithMoreParamsFirst
                    .Where(x => x.Params.Length >= inputArgCount)
                    .FirstOrDefault(x =>
                    {
                        var matchedIndecesMask = 0;
                        return x.Params.Except(x.Params.Where(p =>
                        {
                            var inputArgIndex = funcArgs.IndexOf(t => t == p.ParameterType);
                            if (inputArgIndex == -1 || inputArgIndex == inputArgCount ||
                                (matchedIndecesMask & inputArgIndex << 1) != 0) // input argument was already matched by another parameter
                                return false;
                            matchedIndecesMask |= inputArgIndex << 1;
                            return true;
                        })).All(p => registry.ResolveFactory(request.Push(GetCtorParamResolutionInfo(p, request, registry), p), IfUnresolved.ReturnNull) != null);
                    });

                return matchedCtor.ThrowIfNull(Error.UNABLE_TO_FIND_MATCHING_CTOR_FOR_FUNC_WITH_ARGS, funcType, request).Ctor;
            }
            else
            {
                var matchedCtor = ctorsWithMoreParamsFirst
                    .FirstOrDefault(x => x.Params.All(p => registry.ResolveFactory(request.Push(GetCtorParamResolutionInfo(p, request, registry), p), IfUnresolved.ReturnNull) != null));
                return matchedCtor.ThrowIfNull(Error.UNABLE_TO_FIND_CTOR_WITH_ALL_RESOLVABLE_ARGS, request).Ctor;
            }
        }

        #endregion

        public override Type ImplementationType
        {
            get { return _implementationType; }
        }

        public override bool ProvidesFactoryForRequest
        {
            get { return _implementationType.IsGenericTypeDefinition; }
        }

        public ReflectionFactory(Type implementationType,
            IReuse reuse = null, ConstructorSelector constructorSelector = null, FactorySetup setup = null)
            : base(reuse, setup)
        {
            _implementationType = implementationType.ThrowIfNull()
                .ThrowIf(implementationType.IsAbstract, Error.EXPECTED_NON_ABSTRACT_IMPL_TYPE, implementationType);
            _constructorSelector = constructorSelector;
        }

        public static DependencyResolutionInfo GetCtorParamResolutionInfo(ParameterInfo ctorParam, Request parent, IRegistry registry)
        {
            var serviceKey = parent.ResolvedFactory.Setup.Type != FactoryType.Service ? parent.ServiceKey :
                registry.ResolutionRules.ForConstructorParameterServiceKey.GetFirstNonDefault(r => r(ctorParam, parent, registry));
            return DependencyResolutionInfo.Resolve(ctorParam.ParameterType, serviceKey);
        }

        /// <remarks>Before registering factory checks that ImplementationType is assignable Or
        /// in case of open generics, compatible with <paramref name="serviceType"/>. 
        /// Then checks that there is defined constructor selector for implementation type with multiple/no constructors.</remarks>
        public override void VerifyBeforeRegistration(Type serviceType, IRegistry registry)
        {
            base.VerifyBeforeRegistration(serviceType, registry);

            var implType = _implementationType;
            if (!implType.IsGenericTypeDefinition)
            {
                if (implType.IsGenericType && implType.ContainsGenericParameters)
                    throw Error.USUPPORTED_REGISTRATION_OF_NON_GENERIC_IMPL_TYPE_DEFINITION_BUT_WITH_GENERIC_ARGS.Of(
                        implType, implType.GetGenericTypeDefinition());

                if (implType != serviceType && serviceType != typeof(object) &&
                    Array.IndexOf(implType.GetImplementedTypes(), serviceType) == -1)
                    throw Error.EXPECTED_IMPL_TYPE_ASSIGNABLE_TO_SERVICE_TYPE.Of(implType, serviceType);
            }
            else if (implType != serviceType)
            {
                if (serviceType.IsGenericTypeDefinition)
                {
                    var implementedTypes = implType.GetImplementedTypes();
                    var implementedOpenGenericTypes = implementedTypes.Where(t =>
                        t.IsGenericType && t.ContainsGenericParameters && t.GetGenericTypeDefinition() == serviceType);

                    var implTypeArgs = implType.GetGenericArguments();
                    Throw.If(!implementedOpenGenericTypes.Any(t => t.ContainsAllGenericParameters(implTypeArgs)),
                        Error.UNABLE_TO_REGISTER_OPEN_GENERIC_IMPL_CAUSE_SERVICE_DOES_NOT_SPECIFY_ALL_TYPE_ARGS,
                        implType, serviceType, implementedOpenGenericTypes);
                }
                else if (implType.IsGenericType && serviceType.ContainsGenericParameters)
                    throw Error.USUPPORTED_REGISTRATION_OF_NON_GENERIC_SERVICE_TYPE_DEFINITION_BUT_WITH_GENERIC_ARGS.Of(
                        serviceType, serviceType.GetGenericTypeDefinition());
                else
                    throw Error.UNABLE_TO_REGISTER_OPEN_GENERIC_IMPL_WITH_NON_GENERIC_SERVICE.Of(implType, serviceType);
            }

            if (_constructorSelector == null &&
                registry.ResolutionRules.ConstructorSelector == null)
            {
                var publicCtorCount = implType.GetConstructors().Length;
                if (publicCtorCount != 1)
                    throw Error.UNSPECIFIED_HOWTO_SELECT_CONSTRUCTOR_FOR_IMPLTYPE.Of(implType, publicCtorCount);
            }
        }

        public override Factory GetFactoryForRequestOrDefault(Request request, IRegistry _)
        {
            var closedTypeArgs = _implementationType == request.OpenGenericServiceType
                ? request.ServiceType.GetGenericArguments()
                : GetClosedTypeArgsForGenericImplementationType(_implementationType, request);

            var closedImplType = _implementationType.MakeGenericType(closedTypeArgs);

            return new ReflectionFactory(closedImplType, Reuse, _constructorSelector, Setup);
        }

        public override Expression CreateExpression(Request request, IRegistry registry)
        {
            var ctor = SelectConstructor(_implementationType, request, registry);
            var ctorParams = ctor.GetParameters();

            Expression[] paramExprs = null;
            if (ctorParams.Length != 0)
            {
                paramExprs = new Expression[ctorParams.Length];
                for (var i = 0; i < ctorParams.Length; i++)
                {
                    var param = ctorParams[i];
                    var paramInfo = GetCtorParamResolutionInfo(param, request, registry);
                    if (paramInfo.Action == DependencyResolutionAction.Skip)
                        paramExprs[i] = param.ParameterType.DefaultExpression();
                    else
                    {
                        var paramRequest = request.Push(paramInfo, param);
                        var ifUnresolved = paramInfo.Action == DependencyResolutionAction.Resolve ? IfUnresolved.Throw : IfUnresolved.ReturnNull;
                        var paramFactory = registry.ResolveFactory(paramRequest, ifUnresolved);

                        paramExprs[i] = paramFactory == null
                            ? paramRequest.ServiceType.DefaultExpression()
                            : paramFactory.GetExpression(paramRequest, registry);
                    }
                }
            }

            var newExpr = Expression.New(ctor, paramExprs);
            return InitMembersIfRequired(_implementationType, newExpr, request, registry);
        }

        public override LambdaExpression CreateFuncWithArgsOrDefault(Type funcType, Request request, IRegistry registry, out IList<Type> unusedFuncArgs)
        {
            var funcParamTypes = funcType.GetGenericArguments();
            Throw.If(funcParamTypes.Length == 1, Error.EXPECTED_FUNC_WITH_MULTIPLE_ARGS, funcType);

            var ctor = SelectConstructor(_implementationType, request, registry);
            var ctorParams = ctor.GetParameters();
            var ctorParamExprs = new Expression[ctorParams.Length];
            var funcInputParamExprs = new ParameterExpression[funcParamTypes.Length - 1]; // (minus Func return parameter).

            for (var cp = 0; cp < ctorParams.Length; cp++)
            {
                var ctorParam = ctorParams[cp];
                for (var fp = 0; fp < funcParamTypes.Length - 1; fp++)
                {
                    var funcParamType = funcParamTypes[fp];
                    if (ctorParam.ParameterType == funcParamType &&
                        funcInputParamExprs[fp] == null) // Skip if Func parameter was already used for constructor.
                    {
                        ctorParamExprs[cp] = funcInputParamExprs[fp] = Expression.Parameter(funcParamType, ctorParam.Name);
                        break;
                    }
                }

                if (ctorParamExprs[cp] == null) // If no matching constructor parameter found in Func, resolve it from Container.
                {
                    var resolutionInfo = GetCtorParamResolutionInfo(ctorParam, request, registry);
                    var paramRequest = request.Push(resolutionInfo, ctorParam);
                    ctorParamExprs[cp] = registry.ResolveFactory(paramRequest, IfUnresolved.Throw).GetExpression(paramRequest, registry);
                }
            }

            // Find unused Func parameters (present in Func but not in constructor) and create "_" (ignored) Parameter expressions for them.
            // In addition store unused parameter in output list for client review.
            unusedFuncArgs = null;
            for (var fp = 0; fp < funcInputParamExprs.Length; fp++)
            {
                if (funcInputParamExprs[fp] == null) // unused parameter
                {
                    if (unusedFuncArgs == null)
                        unusedFuncArgs = new List<Type>(2);
                    var funcParamType = funcParamTypes[fp];
                    unusedFuncArgs.Add(funcParamType);
                    funcInputParamExprs[fp] = Expression.Parameter(funcParamType, "_");
                }
            }

            var newExpr = Expression.New(ctor, ctorParamExprs);
            var newExprInitialized = InitMembersIfRequired(_implementationType, newExpr, request, registry);
            return Expression.Lambda(funcType, newExprInitialized, funcInputParamExprs);
        }

        #region Implementation

        private readonly Type _implementationType;
        private readonly ConstructorSelector _constructorSelector;

        private ConstructorInfo SelectConstructor(Type implType, Request request, IRegistry registry)
        {
            var selector = _constructorSelector ?? registry.ResolutionRules.ConstructorSelector;
            if (selector != null)
                return selector(implType, request, registry)
                    .ThrowIfNull(Error.UNABLE_TO_SELECT_CTOR_USING_SELECTOR, implType);

            var ctors = implType.GetConstructors();
            Throw.If(ctors.Length == 0, Error.NO_PUBLIC_CONSTRUCTOR_DEFINED, implType);
            Throw.If(ctors.Length > 1, Error.UNABLE_TO_SELECT_CONSTRUCTOR, ctors.Length, implType);
            return ctors[0];
        }

        private static Expression InitMembersIfRequired(Type implementationType, NewExpression newService, Request request, IRegistry registry)
        {
            var rules = registry.ResolutionRules;
            if (rules.ForPropertyOrFieldWithServiceKey.IsNullOrEmpty())
                return newService;

            var props = implementationType.GetProperties(ResolutionRules.PropertyOrFieldFlags).Where(p => p.GetSetMethod() != null);
            var fields = implementationType.GetFields(ResolutionRules.PropertyOrFieldFlags).Where(f => !f.IsInitOnly);

            var bindings = new List<MemberBinding>();
            foreach (var member in props.Cast<MemberInfo>().Concat(fields.Cast<MemberInfo>()))
            {
                var m = member;
                object memberKey = null;
                if (rules.ForPropertyOrFieldWithServiceKey.GetFirstNonDefault(r => r(out memberKey, m, request, registry)))
                {
                    var memberType = member is PropertyInfo ? ((PropertyInfo)member).PropertyType : ((FieldInfo)member).FieldType;
                    var memberRequest = request.Push(memberType, memberKey, member);
                    var memberExpr = registry.ResolveFactory(memberRequest, IfUnresolved.Throw).GetExpression(memberRequest, registry);
                    bindings.Add(Expression.Bind(member, memberExpr));
                }
            }

            return bindings.Count == 0 ? (Expression)newService : Expression.MemberInit(newService, bindings);
        }

        private static Type[] GetClosedTypeArgsForGenericImplementationType(Type implType, Request request)
        {
            var serviceTypeArgs = request.ServiceType.GetGenericArguments();
            var serviceTypeGenericDefinition = request.OpenGenericServiceType;

            var openImplTypeArgs = implType.GetGenericArguments();
            var implementedTypes = implType.GetImplementedTypes();

            Type[] resultImplTypeArgs = null;
            for (var i = 0; resultImplTypeArgs == null && i < implementedTypes.Length; i++)
            {
                var implementedType = implementedTypes[i];
                if (implementedType.IsGenericType && implementedType.ContainsGenericParameters &&
                    implementedType.GetGenericTypeDefinition() == serviceTypeGenericDefinition)
                {
                    var matchedTypeArgs = new Type[openImplTypeArgs.Length];
                    if (MatchServiceWithImplementedTypeArgs(ref matchedTypeArgs,
                        openImplTypeArgs, implementedType.GetGenericArguments(), serviceTypeArgs))
                        resultImplTypeArgs = matchedTypeArgs;
                }
            }

            resultImplTypeArgs = resultImplTypeArgs.ThrowIfNull(
                Error.UNABLE_TO_MATCH_IMPL_BASE_TYPES_WITH_SERVICE_TYPE, implType, implementedTypes, request);

            var unmatchedArgIndex = Array.IndexOf(resultImplTypeArgs, null);
            if (unmatchedArgIndex != -1)
                throw Error.UNABLE_TO_FIND_OPEN_GENERIC_IMPL_TYPE_ARG_IN_SERVICE.Of(
                    implType, openImplTypeArgs[unmatchedArgIndex], request);

            return resultImplTypeArgs;
        }

        private static bool MatchServiceWithImplementedTypeArgs(ref Type[] matchedImplArgs,
            Type[] openImplementationArgs, Type[] openImplementedArgs, Type[] closedServiceArgs)
        {
            for (var i = 0; i < openImplementedArgs.Length; i++)
            {
                var openImplementedArg = openImplementedArgs[i];
                var closedServiceArg = closedServiceArgs[i];
                if (openImplementedArg.IsGenericParameter)
                {
                    var matchedIndex = openImplementationArgs.IndexOf(t => t.Name == openImplementedArg.Name);
                    if (matchedIndex != -1)
                    {
                        if (matchedImplArgs[matchedIndex] == null)
                            matchedImplArgs[matchedIndex] = closedServiceArg;
                        else if (matchedImplArgs[matchedIndex] != closedServiceArg)
                            return false; // more than one closedServiceArg is matching with single openArg
                    }
                }
                else if (openImplementedArg != closedServiceArg)
                {
                    if (!openImplementedArg.IsGenericType || !openImplementedArg.ContainsGenericParameters ||
                        !closedServiceArg.IsGenericType ||
                        closedServiceArg.GetGenericTypeDefinition() != openImplementedArg.GetGenericTypeDefinition())
                        return false; // openArg and closedArg are different types

                    if (!MatchServiceWithImplementedTypeArgs(ref matchedImplArgs, openImplementationArgs,
                        openImplementedArg.GetGenericArguments(), closedServiceArg.GetGenericArguments()))
                        return false; // nested match failed due either one of above reasons.
                }
            }

            return true;
        }

        #endregion
    }

    public sealed class ExpressionFactory : Factory
    {
        public ExpressionFactory(Func<Request, IRegistry, Expression> expressionFactory, IReuse reuse = null, FactorySetup setup = null)
            : base(reuse, setup)
        {
            _getExpression = expressionFactory.ThrowIfNull();
        }

        public override Expression CreateExpression(Request request, IRegistry registry)
        {
            return _getExpression(request, registry).ThrowIfNull(Error.DELEGATE_FACTORY_EXPRESSION_RETURNED_NULL, request);
        }

        private readonly Func<Request, IRegistry, Expression> _getExpression;
    }

    /// <remarks>This factory is the thin wrapper for user provided delegate 
    /// and where possible will use delegate directly - without converting it to expression.</remarks>
    public sealed class DelegateFactory : Factory
    {
        public DelegateFactory(Func<IResolver, object> customDelegate, IReuse reuse = null, FactorySetup setup = null)
            : base(reuse, setup)
        {
            _customDelegate = customDelegate.ThrowIfNull();
        }

        public override Expression CreateExpression(Request request, IRegistry registry)
        {
            var factoryDelegateExpr = request.ResolutionState.GetItemExpression(_customDelegate);
            var registryExpr = request.ResolutionState.GetItemExpression(registry);
            return Expression.Convert(Expression.Invoke(factoryDelegateExpr, registryExpr), request.ServiceType);
        }

        public override FactoryDelegate GetDelegate(Request request, IRegistry registry)
        {
            if (registry.GetDecoratorExpressionOrDefault(request.ResolveWith(this)) != null)
                return base.GetDelegate(request, registry);

            var registryRefIndex = request.ResolutionState.GetOrAddItem(registry);
            if (Reuse != null)
            {
                var scope = Reuse.GetScope(request, registry);
                var scopeIndex = scope == request.ResolutionScope ? -1
                    : request.ResolutionState.GetOrAddItem(scope);

                return (items, resolutionScope) =>
                    (scopeIndex == -1 ? resolutionScope : (Scope)items.Get(scopeIndex))
                    .GetOrAdd(ID, () => _customDelegate(GetRegistry(items, registryRefIndex)));
            }

            return (items, _) => _customDelegate(GetRegistry(items, registryRefIndex));
        }

        private readonly Func<IResolver, object> _customDelegate;

        private static IRegistry GetRegistry(AppendableArray<object> items, int registryWeakRefIndex)
        {
            return ((RegistryWeakRef)items.Get(registryWeakRefIndex)).Target;
        }
    }

    public sealed class FactoryProvider : Factory
    {
        public override bool ProvidesFactoryForRequest { get { return true; } }

        public FactoryProvider(Func<Request, IRegistry, Factory> getFactoryOrDefault, FactorySetup setup = null)
            : base(setup: setup)
        {
            _getFactoryOrDefault = getFactoryOrDefault.ThrowIfNull();
        }

        public override Factory GetFactoryForRequestOrDefault(Request request, IRegistry registry)
        {
            var factory = _getFactoryOrDefault(request, registry);
            if (factory != null && factory.Setup == DefaultSetup)
                factory.Setup = Setup; // propagate provider setup if it is not specified by client.
            return factory;
        }

        // TODO: Test by using in Unresolved Resolution Rules.
        public override Expression CreateExpression(Request request, IRegistry registry)
        {
            throw new NotSupportedException();
        }

        private readonly Func<Request, IRegistry, Factory> _getFactoryOrDefault;
    }

    public interface IScope
    {
        T GetOrAdd<T>(int id, Func<T> factory);
    }

    public sealed class Scope : IScope, IDisposable
    {
        public T GetOrAdd<T>(int id, Func<T> factory)
        {
            if (_disposed == 1)
                throw Error.SCOPE_IS_DISPOSED.Of();
            lock (_syncRoot)
            {
                var item = _items.GetFirstValueByHashOrDefault(id);
                if (item == null)
                    _items = _items.AddOrUpdate(id, item = factory());
                return (T)item;
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            lock (_syncRoot)
            {
                if (!_items.IsEmpty)
                    foreach (var item in _items.Enumerate().Select(x => x.Value).OfType<IDisposable>())
                        item.Dispose();
                _items = null;
            }
        }

        #region Implementation

        private HashTree<int, object> _items = HashTree<int, object>.Empty;
        private int _disposed;

        // Sync root is required to create object once only. The same reason as for Lazy<T>.
        private readonly object _syncRoot = new object();

        #endregion
    }

    public interface IReuse
    {
        IScope GetScope(Request request, IRegistry registry);
    }

    public sealed class SingletonReuse : IReuse
    {
        public IScope GetScope(Request _, IRegistry registry)
        {
            return registry.SingletonScope;
        }
    }

    public sealed class CurrentScopeReuse : IReuse
    {
        public IScope GetScope(Request _, IRegistry registry)
        {
            return registry.CurrentScope;
        }
    }

    public sealed class ResolutionScopeReuse : IReuse
    {
        public IScope GetScope(Request request, IRegistry _)
        {
            return request.CreateResolutionScope();
        }
    }

    public static class Reuse
    {
        public static readonly IReuse Transient = null; // no reuse.
        public static readonly IReuse Singleton = new SingletonReuse();
        public static readonly IReuse InCurrentScope = new CurrentScopeReuse();
        public static readonly IReuse InResolutionScope = new ResolutionScopeReuse();
    }

    public enum IfUnresolved { Throw, ReturnNull }

    public interface IResolver
    {
        object ResolveDefault(Type serviceType, IfUnresolved ifUnresolved);

        object ResolveKeyed(Type serviceType, object serviceKey, IfUnresolved ifUnresolved);
    }

    public enum IfAlreadyRegistered { ThrowIfDuplicateKey, KeepRegistered, UpdateRegistered }

    public interface IRegistrator
    {
        void Register(Factory factory, Type serviceType, object serviceKey, IfAlreadyRegistered ifAlreadyRegistered);

        bool IsRegistered(Type serviceType, object serviceKey, FactoryType factoryType, Func<Factory, bool> condition);

        void Unregister(Type serviceType, object serviceKey, FactoryType factoryType, Func<Factory, bool> condition);
    }

    public interface IRegistry : IResolver, IRegistrator
    {
        RegistryWeakRef SelfWeakRef { get; }

        ResolutionRules ResolutionRules { get; }

        IScope CurrentScope { get; }
        IScope SingletonScope { get; }

        Factory ResolveFactory(Request request, IfUnresolved ifUnresolved);

        Factory GetFactoryOrDefault(Type serviceType, object serviceKey);
        Factory GetGenericWrapperOrDefault(Type openGenericServiceType);

        Expression GetDecoratorExpressionOrDefault(Request request);

        IEnumerable<KV<object, Factory>> GetAllFactories(Type serviceType);

        Type GetWrappedServiceTypeOrSelf(Type serviceType);
    }

    public sealed class Many<TService>
    {
        public readonly IEnumerable<TService> Items;

        public Many(IEnumerable<TService> items)
        {
            Items = items;
        }
    }

    public sealed class Meta<TService, TMetadata>
    {
        public readonly TService Value;

        public readonly TMetadata Metadata;

        public Meta(TService service, TMetadata metadata)
        {
            Value = service;
            Metadata = metadata;
        }
    }

    [DebuggerDisplay("{Expression}")]
    public sealed class DebugExpression<TService>
    {
        public readonly Expression<FactoryDelegate> Expression;

        public DebugExpression(Expression<FactoryDelegate> expression)
        {
            Expression = expression;
        }
    }

    public class ContainerException : InvalidOperationException
    {
        public ContainerException(string message) : base(message) { }
    }

    public static class Throw
    {
        public static Func<string, Exception> GetException = message => new ContainerException(message);

        public static Func<object, string> PrintArg = PrintTools.Print;

        public static T ThrowIfNull<T>(this T arg, string message = null, object arg0 = null, object arg1 = null, object arg2 = null) where T : class
        {
            if (arg != null) return arg;
            throw GetException(message == null ? Format(ERROR_ARG_IS_NULL, typeof(T)) : Format(message, arg0, arg1, arg2));
        }

        public static T ThrowIf<T>(this T arg, bool throwCondition, string message = null, object arg0 = null, object arg1 = null, object arg2 = null)
        {
            if (!throwCondition) return arg;
            throw GetException(message == null ? Format(ERROR_ARG_HAS_IMVALID_CONDITION, typeof(T)) : Format(message, arg0, arg1, arg2));
        }

        public static T ThrowIf<T>(this T arg, Func<T, Exception> getErrorOrNull)
        {
            var error = getErrorOrNull(arg);
            if (error == null) return arg;
            throw error;
        }

        public static void If(bool throwCondition, string message, object arg0 = null, object arg1 = null, object arg2 = null)
        {
            if (!throwCondition) return;
            throw GetException(Format(message, arg0, arg1, arg2));
        }

        public static Exception Of(this string message, object arg0 = null, object arg1 = null, object arg2 = null)
        {
            return GetException(Format(message, arg0, arg1, arg2));
        }

        public static string Format(this string message, object arg0 = null, object arg1 = null, object arg2 = null)
        {
            return string.Format(message, PrintArg(arg0), PrintArg(arg1), PrintArg(arg2));
        }

        public static readonly string ERROR_ARG_IS_NULL = "Argument of type {0} is null.";
        public static readonly string ERROR_ARG_HAS_IMVALID_CONDITION = "Argument of type {0} has invalid condition.";
    }

    public static class TypeTools
    {
        // ReSharper disable ConstantNullCoalescingCondition
        public static Func<Type, string> PrintDetailsDefault = t => t.FullName ?? t.Name;
        // ReSharper restore ConstantNullCoalescingCondition

        public static string Print(this Type type, Func<Type, string> printDetails = null)
        {
            if (type == null) return null;
            printDetails = printDetails ?? PrintDetailsDefault;
            var name = printDetails(type);
            if (type.IsGenericType) // for generic types
            {
                var genericArgs = type.GetGenericArguments();
                var genericArgsString = type.IsGenericTypeDefinition
                    ? new string(',', genericArgs.Length - 1)
                    : String.Join(", ", genericArgs.Select(x => x.Print(printDetails)).ToArray());
                name = name.Substring(0, name.IndexOf('`')) + "<" + genericArgsString + ">";
            }
            return name.Replace('+', '.'); // for nested classes
        }

        public enum IncludeItself { No, AsFirst }

        /// <summary>
        /// Returns all type interfaces and base types except object.
        /// </summary>
        public static Type[] GetImplementedTypes(this Type type, IncludeItself includeItself = IncludeItself.No)
        {
            Type[] results;

            var interfaces = type.GetInterfaces();
            var interfaceStartIndex = includeItself == IncludeItself.AsFirst ? 1 : 0;
            var selfPlusInterfaceCount = interfaceStartIndex + interfaces.Length;

            var baseType = type.BaseType;
            if (baseType == null || baseType == typeof(object))
                results = new Type[selfPlusInterfaceCount];
            else
            {
                List<Type> baseBaseTypes = null;
                for (var bb = baseType.BaseType; bb != null && bb != typeof(object); bb = bb.BaseType)
                    (baseBaseTypes ?? (baseBaseTypes = new List<Type>(2))).Add(bb);

                if (baseBaseTypes == null)
                    results = new Type[selfPlusInterfaceCount + 1];
                else
                {
                    results = new Type[selfPlusInterfaceCount + 1 + baseBaseTypes.Count];
                    baseBaseTypes.CopyTo(results, selfPlusInterfaceCount + 1);
                }

                results[selfPlusInterfaceCount] = baseType;
            }

            if (includeItself == IncludeItself.AsFirst)
                results[0] = type;

            if (interfaces.Length == 1)
                results[interfaceStartIndex] = interfaces[0];
            else if (interfaces.Length > 1)
                Array.Copy(interfaces, 0, results, interfaceStartIndex, interfaces.Length);

            return results;
        }

        public static bool ContainsAllGenericParameters(this Type similarType, Type[] genericParameters)
        {
            var argNames = new string[genericParameters.Length];
            for (var i = 0; i < genericParameters.Length; i++)
                argNames[i] = genericParameters[i].Name;

            MarkTargetGenericParameters(similarType.GetGenericArguments(), ref argNames);

            for (var i = 0; i < argNames.Length; i++)
                if (argNames[i] != null)
                    return false;

            return true;
        }

        #region Implementation

        private static void MarkTargetGenericParameters(Type[] sourceTypeArgs, ref string[] targetArgNames)
        {
            for (var i = 0; i < sourceTypeArgs.Length; i++)
            {
                var sourceTypeArg = sourceTypeArgs[i];
                if (sourceTypeArg.IsGenericParameter)
                {
                    var matchingTargetArgIndex = Array.IndexOf(targetArgNames, sourceTypeArg.Name);
                    if (matchingTargetArgIndex != -1)
                        targetArgNames[matchingTargetArgIndex] = null;
                }
                else if (sourceTypeArg.IsGenericType && sourceTypeArg.ContainsGenericParameters)
                    MarkTargetGenericParameters(sourceTypeArg.GetGenericArguments(), ref targetArgNames);
            }
        }

        #endregion
    }

    public static class ArrayTools
    {
        public static bool IsNullOrEmpty<T>(this T[] source)
        {
            return source == null || source.Length == 0;
        }

        public static T[] Append<T>(this T[] source, params T[] added)
        {
            if (added == null || added.Length == 0)
                return source;
            if (source == null || source.Length == 0)
                return added;
            var result = new T[source.Length + added.Length];
            Array.Copy(source, 0, result, 0, source.Length);
            if (added.Length == 1)
                result[source.Length] = added[0];
            else
                Array.Copy(added, 0, result, source.Length, added.Length);
            return result;
        }

        public static T[] AppendOrUpdate<T>(this T[] source, T value, int index = -1)
        {
            if (source == null || source.Length == 0)
                return new[] { value };
            var sourceLength = source.Length;
            index = index < 0 ? sourceLength : index;
            var result = new T[index < sourceLength ? sourceLength : sourceLength + 1];
            Array.Copy(source, result, sourceLength);
            result[index] = value;
            return result;
        }

        public static int IndexOf<T>(this T[] source, Func<T, bool> predicate)
        {
            if (source == null || source.Length == 0)
                return -1;
            for (var i = 0; i < source.Length; ++i)
                if (predicate(source[i]))
                    return i;
            return -1;
        }

        public static T[] RemoveAt<T>(this T[] source, int index)
        {
            if (source == null || source.Length == 0 || index < 0 || index >= source.Length)
                return source;
            if (index == 0 && source.Length == 1)
                return new T[0];
            var result = new T[source.Length - 1];
            if (index != 0)
                Array.Copy(source, 0, result, 0, index);
            if (index != result.Length)
                Array.Copy(source, index + 1, result, index, result.Length - index);
            return result;
        }

        public static T[] Remove<T>(this T[] source, T value)
        {
            return source.RemoveAt(source.IndexOf(x => Equals(x, value)));
        }

        public static R GetFirstNonDefault<T, R>(this T[] source, Func<T, R> selector)
        {
            var result = default(R);
            if (source != null && source.Length != 0)
                for (var i = 0; i < source.Length && Equals(result, default(R)); i++)
                    result = selector(source[i]);
            return result;
        }
    }

    public static class PrintTools
    {
        public static string NullOrEmptyStr = "\"\"";
        public static string ItemSeparatorStr = ";" + Environment.NewLine;

        public static string Print(this object x)
        {
            return x is string ? string.Concat("\"", (string)x, "\"")
                 : x is Type ? ((Type)x).Print()
                 : x is IEnumerable<Type> ? ((IEnumerable)x).Print(ItemSeparatorStr, ifEmpty: NullOrEmptyStr)
                 : x is IEnumerable ? ((IEnumerable)x).Print(ItemSeparatorStr, ifEmpty: NullOrEmptyStr)
                 : string.Empty + x;
        }

        public static string Print(this IEnumerable items,
            string separator = ", ", Func<object, string> printItem = null, string ifEmpty = null)
        {
            if (items == null) return null;
            printItem = printItem ?? Print;
            var builder = new StringBuilder();
            foreach (var item in items)
                (builder.Length == 0 ? builder : builder.Append(separator)).Append(printItem(item));
            var result = builder.ToString();
            return result != string.Empty ? result : (ifEmpty ?? string.Empty);
        }
    }

    public static class ExpressionTools
    {
        public static Expression DefaultExpression(this Type type)
        {
            return Expression.Call(_getDefaultMethod.MakeGenericMethod(type), Expression.Constant(type, typeof(Type)));
        }

        private static readonly MethodInfo _getDefaultMethod = typeof(ReflectionFactory).GetMethod("GetDefault");
        public static T GetDefault<T>() { return default(T); }
    }

    public class KV<K, V>
    {
        public readonly K Key;
        public readonly V Value;

        public KV(K key, V value)
        {
            Key = key;
            Value = value;
        }

        public override string ToString()
        {
            return new StringBuilder("[").Append(Key.Print()).Append(", ").Append(Value.Print()).Append("]").ToString();
        }
    }

    public delegate V Update<V>(V oldValue, V newValue);
    public delegate bool ShouldUpdate<V>(V oldValue, out V updatedValue);

    /// <summary>
    /// Immutable kind of http://en.wikipedia.org/wiki/AVL_tree where actual node key is hash code of <typeparamref name="K"/>.
    /// </summary>
    public sealed class HashTree<K, V>
    {
        public static readonly HashTree<K, V> Empty = new HashTree<K, V>();

        public readonly K Key;
        public readonly V Value;

        public readonly int Hash;
        public readonly KV<K, V>[] Conflicts;
        public readonly HashTree<K, V> Left, Right;
        public readonly int Height;

        public bool IsEmpty { get { return Height == 0; } }

        public HashTree<K, V> AddOrUpdate(K key, V value, Update<V> update = null)
        {
            return AddOrUpdate(key.GetHashCode(), key, value, update);
        }

        public V GetValueOrDefault(K key, V defaultValue = default(V))
        {
            var t = this;
            var hash = key.GetHashCode();
            while (t.Height != 0 && t.Hash != hash)
                t = hash < t.Hash ? t.Left : t.Right;
            return t.Height != 0 && (ReferenceEquals(key, t.Key) || key.Equals(t.Key)) ? t.Value
                : t.GetConflictedValueOrDefault(key, defaultValue);
        }

        public V GetFirstValueByHashOrDefault(int hash, V defaultValue = default(V))
        {
            var t = this;
            while (t.Height != 0 && t.Hash != hash)
                t = hash < t.Hash ? t.Left : t.Right;
            return t.Height != 0 ? t.Value : defaultValue;
        }

        /// <remarks>
        /// Depth-first in-order traversal as described in http://en.wikipedia.org/wiki/Tree_traversal
        /// The only difference is using fixed size array instead of stack for speed-up (~20% faster than stack).
        /// </remarks>
        public IEnumerable<KV<K, V>> Enumerate()
        {
            var parents = new HashTree<K, V>[Height];
            var parentCount = -1;
            var t = this;
            while (!t.IsEmpty || parentCount != -1)
            {
                if (!t.IsEmpty)
                {
                    parents[++parentCount] = t;
                    t = t.Left;
                }
                else
                {
                    t = parents[parentCount--];
                    yield return new KV<K, V>(t.Key, t.Value);
                    if (t.Conflicts != null)
                        for (var i = 0; i < t.Conflicts.Length; i++)
                            yield return t.Conflicts[i];
                    t = t.Right;
                }
            }
        }

        /// <remarks>
        /// Based on Eric Lippert http://blogs.msdn.com/b/ericlippert/archive/2008/01/21/immutability-in-c-part-nine-academic-plus-my-avl-tree-implementation.aspx
        /// </remarks>
        public HashTree<K, V> RemoveOrUpdate(K key, ShouldUpdate<V> updateInstead = null)
        {
            return RemoveOrUpdate(key.GetHashCode(), key, updateInstead);
        }

        public HashTree<K, V> Update(K key, V value)
        {
            return RemoveOrUpdate(key.GetHashCode(), key, (V _, out V newValue) =>
            {
                newValue = value;
                return true;
            });
        }

        #region Implementation

        private HashTree() { }

        private HashTree(int hash, K key, V value, KV<K, V>[] conficts, HashTree<K, V> left, HashTree<K, V> right)
        {
            Hash = hash;
            Key = key;
            Value = value;
            Conflicts = conficts;
            Left = left;
            Right = right;
            Height = 1 + (left.Height > right.Height ? left.Height : right.Height);
        }

        private HashTree<K, V> AddOrUpdate(int hash, K key, V value, Update<V> update)
        {
            return Height == 0 ? new HashTree<K, V>(hash, key, value, null, Empty, Empty)
                : (hash == Hash ? UpdateValueAndResolveConflicts(key, value, update)
                : (hash < Hash
                    ? With(Left.AddOrUpdate(hash, key, value, update), Right)
                    : With(Left, Right.AddOrUpdate(hash, key, value, update)))
                        .KeepBalanced());
        }

        private HashTree<K, V> UpdateValueAndResolveConflicts(K key, V value, Update<V> update)
        {
            if (ReferenceEquals(Key, key) || Key.Equals(key))
                return new HashTree<K, V>(Hash, key, update == null ? value : update(Value, value), Conflicts, Left, Right);

            if (Conflicts == null)
                return new HashTree<K, V>(Hash, Key, Value, new[] { new KV<K, V>(key, value) }, Left, Right);

            var i = Conflicts.Length - 1;
            while (i >= 0 && !Equals(Conflicts[i].Key, Key)) i--;
            var conflicts = new KV<K, V>[i != -1 ? Conflicts.Length : Conflicts.Length + 1];
            Array.Copy(Conflicts, 0, conflicts, 0, Conflicts.Length);
            conflicts[i != -1 ? i : Conflicts.Length] =
                new KV<K, V>(key, i != -1 && update != null ? update(Conflicts[i].Value, value) : value);
            return new HashTree<K, V>(Hash, Key, Value, conflicts, Left, Right);
        }

        private V GetConflictedValueOrDefault(K key, V defaultValue)
        {
            if (Conflicts != null)
                for (var i = 0; i < Conflicts.Length; i++)
                    if (Equals(Conflicts[i].Key, key))
                        return Conflicts[i].Value;
            return defaultValue;
        }

        private HashTree<K, V> RemoveOrUpdate(int hash, K key, ShouldUpdate<V> updateInstead = null, bool ignoreKey = false)
        {
            if (Height == 0)
                return this;

            HashTree<K, V> result;
            if (hash == Hash) // found matched Node
            {
                if (ignoreKey || Equals(Key, key))
                {
                    if (!ignoreKey)
                    {
                        V updatedValue;
                        if (updateInstead != null && updateInstead(Value, out updatedValue))
                            return new HashTree<K, V>(Hash, Key, updatedValue, Conflicts, Left, Right);

                        if (Conflicts != null)
                        {
                            if (Conflicts.Length == 1)
                                return new HashTree<K, V>(Hash, Conflicts[0].Key, Conflicts[0].Value, null, Left, Right);
                            var shrinkedConflicts = new KV<K, V>[Conflicts.Length - 1];
                            Array.Copy(Conflicts, 1, shrinkedConflicts, 0, shrinkedConflicts.Length);
                            return new HashTree<K, V>(Hash, Conflicts[0].Key, Conflicts[0].Value, shrinkedConflicts, Left, Right);
                        }
                    }

                    if (Height == 1) // remove node
                        return Empty;

                    if (Right.IsEmpty)
                        result = Left;
                    else if (Left.IsEmpty)
                        result = Right;
                    else
                    {
                        // we have two children, so remove the next highest node and replace this node with it.
                        var successor = Right;
                        while (!successor.Left.IsEmpty) successor = successor.Left;
                        result = successor.With(Left, Right.RemoveOrUpdate(successor.Hash, default(K), ignoreKey: true));
                    }
                }
                else if (Conflicts != null)
                {
                    var index = Conflicts.Length - 1;
                    while (index >= 0 && !Equals(Conflicts[index].Key, key)) --index;
                    if (index == -1)        // key is not found in conflicts - just return
                        return this;

                    V updatedValue;
                    var conflict = Conflicts[index];
                    if (updateInstead != null && updateInstead(conflict.Value, out updatedValue))
                    {
                        var updatedConflicts = new KV<K, V>[Conflicts.Length];
                        Array.Copy(Conflicts, 0, updatedConflicts, 0, updatedConflicts.Length);
                        updatedConflicts[index] = new KV<K, V>(conflict.Key, updatedValue);
                        return new HashTree<K, V>(Hash, Key, Value, updatedConflicts, Left, Right);
                    }

                    if (Conflicts.Length == 1)
                        return new HashTree<K, V>(Hash, Key, Value, null, Left, Right);
                    var shrinkedConflicts = new KV<K, V>[Conflicts.Length - 1];
                    var newIndex = 0;
                    for (var i = 0; i < Conflicts.Length; ++i)
                        if (i != index) shrinkedConflicts[newIndex++] = Conflicts[i];
                    return new HashTree<K, V>(Hash, Key, Value, shrinkedConflicts, Left, Right);
                }
                else return this; // if key is not matching and no conflicts to lookup - just return
            }
            else if (hash < Hash)
                result = With(Left.RemoveOrUpdate(hash, key, updateInstead, ignoreKey), Right);
            else
                result = With(Left, Right.RemoveOrUpdate(hash, key, updateInstead, ignoreKey));
            return result.KeepBalanced();
        }

        private HashTree<K, V> KeepBalanced()
        {
            var delta = Left.Height - Right.Height;
            return delta >= 2 ? With(Left.Right.Height - Left.Left.Height == 1 ? Left.RotateLeft() : Left, Right).RotateRight()
                : (delta <= -2 ? With(Left, Right.Left.Height - Right.Right.Height == 1 ? Right.RotateRight() : Right).RotateLeft()
                : this);
        }

        private HashTree<K, V> RotateRight()
        {
            return Left.With(Left.Left, With(Left.Right, Right));
        }

        private HashTree<K, V> RotateLeft()
        {
            return Right.With(With(Left, Right.Left), Right.Right);
        }

        private HashTree<K, V> With(HashTree<K, V> left, HashTree<K, V> right)
        {
            return new HashTree<K, V>(Hash, Key, Value, Conflicts, left, right);
        }

        #endregion
    }

    public static class Ref
    {
        public static Ref<T> Of<T>(T value = default(T)) where T : class
        {
            return new Ref<T>(value);
        }

        /// <remarks>
        /// First, it evaluates new value using <paramref name="getValue"/> function. 
        /// Second, it checks that original value is not changed. 
        /// If it is changed it will retry first step, otherwise it assigns new value and returns original (the one used for <paramref name="getValue"/>).
        /// </remarks>
        public static T Swap<T>(ref T value, Func<T, T> getValue) where T : class
        {
            var retryCount = 0;
            while (true)
            {
                var oldValue = value;
                var newValue = getValue(oldValue);
                if (Interlocked.CompareExchange(ref value, newValue, oldValue) == oldValue)
                    return oldValue;
                if (++retryCount > RETRY_COUNT_UNTIL_THROW)
                    throw new InvalidOperationException(ERROR_RETRY_COUNT_EXCEEDED);
            }
        }

        private const int RETRY_COUNT_UNTIL_THROW = 10;
        private static readonly string ERROR_RETRY_COUNT_EXCEEDED =
            "Ref retried to Update for " + RETRY_COUNT_UNTIL_THROW + " times But there is always someone else intervened.";
    }

    public sealed class Ref<T> where T : class
    {
        public T Value { get { return _value; } }

        public Ref(T initialValue = default(T))
        {
            _value = initialValue;
        }

        public T Swap(Func<T, T> getValue)
        {
            return Ref.Swap(ref _value, getValue);
        }

        private T _value;
    }
}