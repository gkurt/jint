using System.Threading;
using Jint.Native;
using Jint.Native.Object;
using Jint.Native.Promise;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using Jint.Runtime.Interpreter;
using Jint.Runtime.Modules;
using Module = Jint.Runtime.Modules.Module;

namespace Jint;

public partial class Engine
{
    public ModuleOperations Modules { get; internal set; } = null!;

    /// <summary>
    /// https://tc39.es/ecma262/#sec-getactivescriptormodule
    /// </summary>
    internal IScriptOrModule? GetActiveScriptOrModule()
    {
        return _executionContexts?.GetActiveScriptOrModule();
    }

    // Cache key for the per-engine module map. Per the import-attributes spec,
    // two requests for the same resolved specifier with different attributes
    // are distinct module records. Import phase (defer/source) does NOT create
    // distinct records — defer and evaluation share the same underlying module.
    internal readonly record struct ModuleCacheKey(string Key, ModuleImportAttribute[] Attributes)
    {
        public static ModuleCacheKey From(ResolvedSpecifier resolved)
            => new(resolved.Key, resolved.ModuleRequest.Attributes ?? []);

        public bool Equals(ModuleCacheKey other)
        {
            return string.Equals(Key, other.Key, StringComparison.Ordinal)
                   && ModuleRequest.AttributesEqual(Attributes, other.Attributes);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (StringComparer.Ordinal.GetHashCode(Key) * 397) ^ Attributes.Length;
            }
        }
    }

    /// <summary>
    /// Keys the <c>[[LoadedModules]]</c> list of a referrer that is not itself a module — a script, or the
    /// host calling <see cref="ModuleOperations.Import(string)"/>. Such a referrer has nowhere to keep the
    /// list, so the engine keeps it, and the location is part of the key because two scripts at different
    /// locations may resolve one specifier to two different modules.
    /// </summary>
    internal readonly record struct ScriptLoadedModuleKey(string? ReferrerLocation, ModuleRequest Request)
    {
        public bool Equals(ScriptLoadedModuleKey other)
            => string.Equals(ReferrerLocation, other.ReferrerLocation, StringComparison.Ordinal)
               && LoadedModuleRequestComparer.Instance.Equals(Request, other.Request);

        public override int GetHashCode()
        {
            unchecked
            {
                var locationHash = ReferrerLocation is null ? 0 : StringComparer.Ordinal.GetHashCode(ReferrerLocation);
                return (locationHash * 397) ^ LoadedModuleRequestComparer.Instance.GetHashCode(Request);
            }
        }
    }

    public class ModuleOperations
    {
        private readonly Engine _engine;
        private readonly Dictionary<ModuleCacheKey, Module> _modules = new();
        private readonly Dictionary<string, ModuleBuilder> _builders = new(StringComparer.Ordinal);
        private readonly Dictionary<ScriptLoadedModuleKey, Module> _scriptLoadedModules = new();

        /// <summary>
        /// The loads an asynchronous loader has started but not finished, keyed by resolved specifier. Two
        /// referrers importing the same file — the ordinary diamond — must not become two fetches, so the
        /// second one attaches to the first load's completion instead of asking the loader again.
        /// </summary>
        private Dictionary<ModuleCacheKey, ModuleLoadCompletion>? _pendingLoads;

        public ModuleOperations(Engine engine, IModuleLoader moduleLoader)
        {
            ModuleLoader = moduleLoader;
            AsyncModuleLoader = moduleLoader as IAsyncModuleLoader;
            _engine = engine;
        }

        internal IModuleLoader ModuleLoader { get; }

        /// <summary>
        /// The registered loader's asynchronous face, or null when it only loads synchronously. Non-null is
        /// what switches the engine onto the asynchronous load path.
        /// </summary>
        internal IAsyncModuleLoader? AsyncModuleLoader { get; }

        internal Engine Engine => _engine;

        internal Module Load(string? referencingModuleLocation, ModuleRequest request)
        {
            var moduleResolution = ModuleLoader.Resolve(referencingModuleLocation, request);
            var cacheKey = ModuleCacheKey.From(moduleResolution);

            if (_modules.TryGetValue(cacheKey, out var module))
            {
                return module;
            }

            if (_builders.TryGetValue(moduleResolution.Key, out var moduleBuilder))
            {
                module = LoadFromBuilder(moduleResolution.Key, moduleBuilder, cacheKey);
            }
            else
            {
                module = LoadFromModuleLoader(moduleResolution, cacheKey);
            }

            return module;
        }

        /// <summary>
        /// https://tc39.es/ecma262/#sec-HostLoadImportedModule
        /// </summary>
        /// <remarks>
        /// Resolution is synchronous even for an asynchronous loader — the spec's HostLoadImportedModule maps
        /// a referrer/specifier pair to a module record, and only the fetching of an unseen one is allowed to
        /// take time. Everything already known is therefore finished inline: an entry in the referrer's
        /// <c>[[LoadedModules]]</c>, a module already in this engine's registry, or a
        /// <see cref="Add(string,string)"/>ed builder.
        /// </remarks>
        internal void LoadImportedModule(IScriptOrModule? referrer, ModuleRequest request, ModuleLoadPayload payload)
            => LoadImportedModule(referrer, referrer?.Location, request, payload);

        internal void LoadImportedModule(IScriptOrModule? referrer, string? referrerLocation, ModuleRequest request, ModuleLoadPayload payload)
        {
            if (TryGetLoadedModule(referrer, referrerLocation, request, out var loaded))
            {
                Finish(loaded, error: null);
                return;
            }

            ResolvedSpecifier moduleResolution;
            try
            {
                moduleResolution = ModuleLoader.Resolve(referrerLocation, request);
            }
            catch (JavaScriptException ex)
            {
                Finish(module: null, ex.Error);
                return;
            }

            var cacheKey = ModuleCacheKey.From(moduleResolution);

            if (_modules.TryGetValue(cacheKey, out var module))
            {
                Finish(module, error: null);
                return;
            }

            if (_builders.TryGetValue(moduleResolution.Key, out var moduleBuilder))
            {
                Finish(LoadFromBuilder(moduleResolution.Key, moduleBuilder, cacheKey), error: null);
                return;
            }

            if (_pendingLoads is not null && _pendingLoads.TryGetValue(cacheKey, out var inFlight))
            {
                inFlight.AddWaiter(referrer, referrerLocation, request, payload);
                return;
            }

            if (AsyncModuleLoader is not null)
            {
                var completion = new ModuleLoadCompletion(this, moduleResolution, cacheKey);
                completion.AddWaiter(referrer, referrerLocation, request, payload);
                (_pendingLoads ??= new Dictionary<ModuleCacheKey, ModuleLoadCompletion>())[cacheKey] = completion;

                try
                {
                    AsyncModuleLoader.LoadModuleAsync(_engine, moduleResolution, completion);
                }
                catch (Exception ex)
                {
                    // A loader that throws instead of reporting through the completion still has to end up as
                    // a rejection rather than an exception on whatever thread happened to be evaluating.
                    completion.SetError(ex);
                }

                return;
            }

            try
            {
                Finish(LoadFromModuleLoader(moduleResolution, cacheKey), error: null);
            }
            catch (JavaScriptException ex)
            {
                Finish(module: null, ex.Error);
            }

            void Finish(Module? module, JsValue? error)
                => _engine._host.FinishLoadingImportedModule(referrer, referrerLocation, request, payload, module, error);
        }

        /// <summary>
        /// Looks a request up in the referrer's <c>[[LoadedModules]]</c>.
        /// </summary>
        internal bool TryGetLoadedModule(IScriptOrModule? referrer, ModuleRequest request, out Module module)
            => TryGetLoadedModule(referrer, referrer?.Location, request, out module);

        internal bool TryGetLoadedModule(IScriptOrModule? referrer, string? referrerLocation, ModuleRequest request, out Module module)
        {
            if (referrer is Module referrerModule)
            {
                return referrerModule.TryGetLoadedModule(request, out module);
            }

            return _scriptLoadedModules.TryGetValue(new ScriptLoadedModuleKey(referrerLocation, request), out module!);
        }

        /// <summary>
        /// Step 1 of <see href="https://tc39.es/ecma262/#sec-FinishLoadingImportedModule">FinishLoadingImportedModule</see>.
        /// </summary>
        internal void RecordLoadedModule(IScriptOrModule? referrer, string? referrerLocation, ModuleRequest request, Module module)
        {
            if (referrer is Module referrerModule)
            {
                referrerModule.RecordLoadedModule(request, module);
                return;
            }

            var key = new ScriptLoadedModuleKey(referrerLocation, request);
            if (_scriptLoadedModules.TryGetValue(key, out var existing))
            {
                if (!ReferenceEquals(existing, module))
                {
                    Throw.InvalidOperationException(
                        $"Error while loading module: the module loader returned two different modules for specifier '{request.Specifier}'. HostLoadImportedModule must be consistent for a given referrer and specifier.");
                }

                return;
            }

            _scriptLoadedModules[key] = module;
        }

        /// <summary>
        /// Registers a module the engine has just obtained, under the resolved specifier every referrer that
        /// resolves to it will look it up by.
        /// </summary>
        internal void RegisterModule(ModuleCacheKey cacheKey, Module module)
        {
            _modules[cacheKey] = module;

            if (module is SourceTextModule sourceTextModule)
            {
                _engine.Debugger.OnBeforeEvaluate(sourceTextModule._source);
            }
        }

        internal void RemovePendingLoad(ModuleCacheKey cacheKey) => _pendingLoads?.Remove(cacheKey);

        /// <summary>
        /// Forgets every load an asynchronous loader has not finished. Called when the engine ends an
        /// evaluation cycle (<see cref="AdvancedOperations.RestoreGlobalSnapshot"/>): the completion of such a
        /// load is already fenced off by the event loop's generation and will be discarded, so leaving the
        /// entry behind would let the next cycle attach to a load that can never finish.
        /// </summary>
        internal void DiscardPendingLoads() => _pendingLoads?.Clear();

        private BuilderModule LoadFromBuilder(string specifier, ModuleBuilder moduleBuilder, ModuleCacheKey cacheKey)
        {
            var parsedModule = moduleBuilder.Parse();
            var hasTopLevelAwait = HoistingScope.HasTopLevelAwait(parsedModule.Program!);
            var module = new BuilderModule(_engine, _engine.Realm, in parsedModule, location: parsedModule.Program!.Location.SourceFile, async: hasTopLevelAwait);
            RegisterModule(cacheKey, module);
            moduleBuilder.BindExportedValues(module);
            _builders.Remove(specifier);
            return module;
        }

        private Module LoadFromModuleLoader(ResolvedSpecifier moduleResolution, ModuleCacheKey cacheKey)
        {
            var module = ModuleLoader.LoadModule(_engine, moduleResolution);
            RegisterModule(cacheKey, module);
            return module;
        }

        public void Add(string specifier, string code)
        {
            var moduleBuilder = new ModuleBuilder(_engine, specifier);
            moduleBuilder.AddSource(code);
            Add(specifier, moduleBuilder);
        }

        public void Add(string specifier, Action<ModuleBuilder> buildModule)
        {
            var moduleBuilder = new ModuleBuilder(_engine, specifier);
            buildModule(moduleBuilder);
            Add(specifier, moduleBuilder);
        }

        public void Add(string specifier, ModuleBuilder moduleBuilder)
        {
            _builders.Add(specifier, moduleBuilder);
        }

        /// <summary>
        /// Imports a module and returns its namespace, blocking until the whole graph has loaded, linked and
        /// evaluated.
        /// </summary>
        /// <remarks>
        /// With an <see cref="IAsyncModuleLoader"/> the calling thread is what drives the engine's event loop
        /// while the loads are in flight, so this deadlocks if the loader's own completions need that same
        /// thread — a Unity main thread delivering web requests through a coroutine, for instance. Use
        /// <see cref="ImportAsync(string,CancellationToken)"/> or <see cref="StartImport(string)"/> there.
        /// </remarks>
        public ObjectInstance Import(string specifier)
        {
            return Import(specifier, referencingModuleLocation: null);
        }

        internal ObjectInstance Import(string specifier, string? referencingModuleLocation)
        {
            return Import(new ModuleRequest(specifier, []), referencingModuleLocation);
        }

        internal ObjectInstance Import(ModuleRequest request, string? referencingModuleLocation)
        {
            var module = LoadRootModule(request, referencingModuleLocation);

            // The specification's load phase. Everything the module imports has to be present before it can
            // be linked, and with an asynchronous loader "present" is not something the engine can arrange by
            // itself: the load promise settles on some later turn of the event loop, so the caller of a
            // synchronous Import is the thread that has to run those turns.
            RunLoadPhaseBlocking(module);

            if (module is not CyclicModule cyclicModule)
            {
                LinkModule(request.Specifier, module);
                EvaluateModule(request.Specifier, module);
            }
            else if (cyclicModule.Status == ModuleStatus.Unlinked)
            {
                LinkModule(request.Specifier, cyclicModule);

                if (cyclicModule.Status == ModuleStatus.Linked)
                {
                    _engine.ExecuteWithConstraints(true, () => EvaluateModule(request.Specifier, cyclicModule));
                }

                if (cyclicModule.Status != ModuleStatus.Evaluated)
                {
                    Throw.NotSupportedException($"Error while evaluating module: Module is in an invalid state: '{cyclicModule.Status}'");
                }
            }
            else if (cyclicModule.Status == ModuleStatus.Evaluated)
            {
                // The module has already been evaluated - either as its own entry point or as a
                // dependency of some other graph. https://tc39.es/ecma262/#sec-ContinueDynamicImport
                // evaluates it again regardless, and https://tc39.es/ecma262/#sec-moduleevaluation
                // makes that a no-op that hands back the already settled promise, with one exception:
                // a module whose evaluation threw replays its recorded [[EvaluationError]]. Skipping
                // the call returned a namespace over bindings the failed evaluation never initialized.
                _engine.ExecuteWithConstraints(true, () => EvaluateModule(request.Specifier, cyclicModule));
            }

            _engine.RunAvailableContinuations();

            return Module.GetModuleNamespace(module);
        }

        /// <summary>
        /// Starts an import and returns at once, without blocking and without needing a thread of its own. The
        /// engine only makes progress on it when it is given turns, so the host drives it by calling
        /// <see cref="AdvancedOperations.ProcessTasks"/> — from a game loop or a UI message pump, say — until
        /// the returned operation reports <see cref="ModuleImportOperation.IsCompleted"/>.
        /// </summary>
        /// <remarks>
        /// This is the same pipeline a dynamic <c>import()</c> inside script goes through, and
        /// <see cref="ModuleImportOperation.Promise"/> is the promise it settles into.
        /// </remarks>
        public ModuleImportOperation StartImport(string specifier) => StartImport(specifier, referencingModuleLocation: null);

        /// <inheritdoc cref="StartImport(string)" />
        public ModuleImportOperation StartImport(string specifier, string? referencingModuleLocation)
        {
            var request = new ModuleRequest(specifier, []);
            var capability = PromiseConstructor.NewPromiseCapability(_engine, _engine.Realm.Intrinsics.Promise);
            var payload = new DynamicImportPayload(_engine, request, capability);

            try
            {
                _engine._host.LoadImportedModule(referrer: null, referencingModuleLocation, request, payload);
            }
            catch (JavaScriptException ex)
            {
                capability.Reject(ex.Error);
            }

            var operation = new ModuleImportOperation(capability.PromiseInstance);

            // Reactions rather than polling, so that the operation is also what marks the rejection handled and
            // a failed import does not read as an unhandled promise rejection to a host watching for those.
            var onFulfilled = new ClrFunction(_engine, "", (_, args) =>
            {
                operation.Fulfil(args.At(0));
                return JsValue.Undefined;
            }, 1, PropertyFlag.Configurable);

            var onRejected = new ClrFunction(_engine, "", (_, args) =>
            {
                operation.Fail(args.At(0));
                return JsValue.Undefined;
            }, 1, PropertyFlag.Configurable);

            PromiseOperations.PerformPromiseThen(_engine, (JsPromise) capability.PromiseInstance, onFulfilled, onRejected, resultCapability: null!);

            return operation;
        }

        /// <summary>
        /// Imports a module and returns its namespace, without blocking a thread while the module graph loads
        /// or while a top-level <c>await</c> is outstanding. The natural entry point for an
        /// <see cref="IAsyncModuleLoader"/>.
        /// </summary>
        /// <remarks>
        /// The engine is single-threaded and this method does not change that: continuations run one at a time,
        /// on whichever thread resumes the await. A host with a thread affinity — a game loop, a UI thread —
        /// wants <see cref="StartImport(string)"/> and its own pump instead, so that every turn runs where the
        /// host needs it to.
        /// </remarks>
        /// <exception cref="PromiseRejectedException">The module failed to load or its evaluation threw.</exception>
        public Task<ObjectInstance> ImportAsync(string specifier, CancellationToken cancellationToken = default)
            => ImportAsync(specifier, referencingModuleLocation: null, cancellationToken);

        /// <inheritdoc cref="ImportAsync(string,CancellationToken)" />
        public async Task<ObjectInstance> ImportAsync(string specifier, string? referencingModuleLocation, CancellationToken cancellationToken = default)
        {
            var promise = StartImport(specifier, referencingModuleLocation).Promise;
            var result = await _engine.UnwrapResultAsync(promise, cancellationToken).ConfigureAwait(false);
            return (ObjectInstance) result;
        }

        /// <summary>
        /// Loads the root of a module graph: a <c>HostLoadImportedModule</c> call like any other, so with an
        /// asynchronous loader it may only finish on a later turn of the event loop.
        /// </summary>
        private Module LoadRootModule(ModuleRequest request, string? referencingModuleLocation)
        {
            var payload = new RootModuleLoadPayload();
            _engine._host.LoadImportedModule(referrer: null, referencingModuleLocation, request, payload);

            if (!payload.IsCompleted)
            {
                var timeout = _engine.Options.Constraints.PromiseTimeout;
                if (!_engine.DrainEventLoopUntil(static state => ((RootModuleLoadPayload) state).IsCompleted, payload, completedEvent: null, timeout))
                {
                    Throw.TimeoutException($"Timeout of {timeout} reached while loading module '{request.Specifier}'. An asynchronous module loader did not finish the load in time.");
                }
            }

            if (payload.Error is not null)
            {
                Throw.JavaScriptException(_engine, payload.Error, in AstExtensions.DefaultLocation);
            }

            return payload.Module!;
        }

        /// <summary>
        /// Runs <see cref="Module.LoadRequestedModules"/> and, for an asynchronous loader, drives the event
        /// loop until the graph is in. Throws whatever the load failed with.
        /// </summary>
        private void RunLoadPhaseBlocking(Module module)
        {
            if (module.LoadRequestedModules() is not JsPromise loadPromise)
            {
                return;
            }

            if (loadPromise.State == PromiseState.Pending)
            {
                _engine.DrainEventLoopUntilSettled(loadPromise, _engine.Options.Constraints.PromiseTimeout);
            }

            switch (loadPromise.State)
            {
                case PromiseState.Rejected:
                    Throw.JavaScriptException(_engine, loadPromise.Value, in AstExtensions.DefaultLocation);
                    break;
                case PromiseState.Pending:
                    Throw.TimeoutException($"Timeout of {_engine.Options.Constraints.PromiseTimeout} reached while loading the module graph. An asynchronous module loader did not finish the load in time.");
                    break;
            }
        }

        private static void LinkModule(string specifier, Module module)
        {
            module.Link();
        }

        private JsValue EvaluateModule(string specifier, Module module)
        {
            var ownsContext = _engine._activeEvaluationContext is null;
            _engine._activeEvaluationContext ??= new EvaluationContext(_engine);
            JsValue evaluationResult;
            try
            {
                evaluationResult = module.Evaluate();
            }
            finally
            {
                if (ownsContext)
                {
                    _engine._activeEvaluationContext = null!;
                }
            }

            // This should instead be returned and resolved in ImportModule(specifier) only so Host.ImportModuleDynamically can use this promise
            if (evaluationResult is not JsPromise promise)
            {
                Throw.InvalidOperationException($"Error while evaluating module: Module evaluation did not return a promise: {evaluationResult.Type}");
                return null;
            }

            // For async modules (top-level await), drive the event loop until the module's top-level
            // capability promise settles. A .NET Task awaited at the top level (task interop) completes
            // on a ThreadPool thread and enqueues its resolve job only after a delay, so we must poll
            // rather than spin — a tight loop finishes in microseconds while the queue is still empty
            // and gives up before the Task ever completes (issue #2663). Bounded by PromiseTimeout so a
            // genuinely never-settling top-level await cannot block Import forever.
            _engine.DrainEventLoopUntilSettled(promise, _engine.Options.Constraints.PromiseTimeout);

            if (promise.State == PromiseState.Rejected)
            {
                var location = module is CyclicModule cyclicModuleRecord
                    ? cyclicModuleRecord.AbnormalCompletionLocation
                    : SourceLocation.From(new Position(), new Position());

                var node = AstExtensions.CreateLocationNode(in location);
                Throw.JavaScriptException(_engine, promise.Value, node.Location);
            }
            else if (promise.State != PromiseState.Fulfilled)
            {
                Throw.InvalidOperationException($"Error while evaluating module: Module evaluation did not return a fulfilled promise: {promise.State}");
            }

            return evaluationResult;
        }
    }
}
