using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;

namespace EmotionCat
{
    /// Minimal ONNX Runtime 1.23 C API binding; no managed NuGet dependencies for csc builds.
    /// Function-table indices are OrtApi member positions verified with offsetof() against onnxruntime_c_api.h.
    internal sealed class OnnxRuntime
    {
        private const uint ApiVersion = 23;
        private const int IdxGetErrorMessage = 2, IdxCreateEnv = 3, IdxDisableTelemetry = 6, IdxCreateSession = 7, IdxRun = 9,
            IdxCreateSessionOptions = 10, IdxSetExecutionMode = 13, IdxDisableMemPattern = 17,
            IdxSetGraphOptimization = 23, IdxSetIntraOpThreads = 24,
            IdxSetInterOpThreads = 25, IdxCreateRunOptions = 39, IdxRunOptionsSetTerminate = 46,
            IdxCreateTensorWithData = 49, IdxGetTensorMutableData = 51,
            IdxCreateCpuMemoryInfo = 69, IdxReleaseEnv = 92, IdxReleaseStatus = 93, IdxReleaseMemoryInfo = 94,
            IdxReleaseSession = 95, IdxReleaseValue = 96, IdxReleaseRunOptions = 97, IdxReleaseSessionOptions = 100,
            IdxAddSessionConfigEntry = 130, IdxGetExecutionProviderApi = 195;
        internal const int TensorFloat = 1, TensorInt64 = 7, TensorBool = 9;

        [DllImport("onnxruntime.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr OrtGetApiBase();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetApiFn(uint version);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetErrorMessageFn(IntPtr status);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr CreateEnvFn(int level, [MarshalAs(UnmanagedType.LPStr)] string id, out IntPtr env);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr CreateSessionFn(IntPtr env, [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr options, out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr RunFn(IntPtr session, IntPtr runOptions, IntPtr[] inputNames, IntPtr[] inputs, UIntPtr inputCount, IntPtr[] outputNames, UIntPtr outputCount, IntPtr[] outputs);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr CreateOptionsFn(out IntPtr options);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr SetIntFn(IntPtr options, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr OptionsFn(IntPtr options);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr ConfigFn(IntPtr options, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPStr)] string value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr ProviderApiFn([MarshalAs(UnmanagedType.LPStr)] string name, uint version, out IntPtr api);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr AppendDmlFn(IntPtr options, ref DmlDeviceOptions device);
        [StructLayout(LayoutKind.Sequential)] private struct DmlDeviceOptions { public uint Preference, Filter; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr CreateTensorFn(IntPtr info, IntPtr data, UIntPtr bytes, long[] shape, UIntPtr rank, int type, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetDataFn(IntPtr value, out IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr CreateCpuInfoFn(int allocator, int memType, out IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ReleaseFn(IntPtr handle);

        private readonly GetErrorMessageFn getErrorMessage;
        private readonly CreateEnvFn createEnv;
        private readonly CreateSessionFn createSession;
        private readonly RunFn run;
        private readonly CreateOptionsFn createOptions, createRunOptions;
        private readonly SetIntFn setGraphOptimization, setIntraThreads, setInterThreads;
        private readonly SetIntFn setExecutionMode;
        private readonly OptionsFn disableMemPattern, disableTelemetry, terminateRun;
        private readonly ConfigFn addConfig;
        private readonly ProviderApiFn getProviderApi;
        private readonly CreateTensorFn createTensor;
        private readonly GetDataFn getTensorData;
        private readonly CreateCpuInfoFn createCpuInfo;
        internal readonly Action<IntPtr> ReleaseEnv, ReleaseStatus, ReleaseMemoryInfo, ReleaseSession, ReleaseValue, ReleaseSessionOptions;
        private readonly Action<IntPtr> releaseRunOptions;

        private static OnnxRuntime instance;
        private static readonly object InstanceGate = new object();

        internal static OnnxRuntime Instance
        {
            get { lock (InstanceGate) return instance ?? (instance = new OnnxRuntime()); }
        }

        private OnnxRuntime()
        {
            IntPtr apiBase = OrtGetApiBase();
            var getApi = Fn<GetApiFn>(Marshal.ReadIntPtr(apiBase, 0));
            IntPtr api = getApi(ApiVersion);
            if (api == IntPtr.Zero) throw new InvalidOperationException("onnxruntime.dll 버전이 맞지 않습니다.");
            Func<int, IntPtr> slot = index => Marshal.ReadIntPtr(api, index * IntPtr.Size);
            getErrorMessage = Fn<GetErrorMessageFn>(slot(IdxGetErrorMessage));
            createEnv = Fn<CreateEnvFn>(slot(IdxCreateEnv));
            disableTelemetry = Fn<OptionsFn>(slot(IdxDisableTelemetry));
            createSession = Fn<CreateSessionFn>(slot(IdxCreateSession));
            run = Fn<RunFn>(slot(IdxRun));
            createOptions = Fn<CreateOptionsFn>(slot(IdxCreateSessionOptions));
            createRunOptions = Fn<CreateOptionsFn>(slot(IdxCreateRunOptions));
            terminateRun = Fn<OptionsFn>(slot(IdxRunOptionsSetTerminate));
            releaseRunOptions = Release(slot(IdxReleaseRunOptions));
            setGraphOptimization = Fn<SetIntFn>(slot(IdxSetGraphOptimization));
            setIntraThreads = Fn<SetIntFn>(slot(IdxSetIntraOpThreads));
            setInterThreads = Fn<SetIntFn>(slot(IdxSetInterOpThreads));
            setExecutionMode = Fn<SetIntFn>(slot(IdxSetExecutionMode));
            disableMemPattern = Fn<OptionsFn>(slot(IdxDisableMemPattern));
            addConfig = Fn<ConfigFn>(slot(IdxAddSessionConfigEntry));
            getProviderApi = Fn<ProviderApiFn>(slot(IdxGetExecutionProviderApi));
            createTensor = Fn<CreateTensorFn>(slot(IdxCreateTensorWithData));
            getTensorData = Fn<GetDataFn>(slot(IdxGetTensorMutableData));
            createCpuInfo = Fn<CreateCpuInfoFn>(slot(IdxCreateCpuMemoryInfo));
            ReleaseEnv = Release(slot(IdxReleaseEnv));
            ReleaseStatus = Release(slot(IdxReleaseStatus));
            ReleaseMemoryInfo = Release(slot(IdxReleaseMemoryInfo));
            ReleaseSession = Release(slot(IdxReleaseSession));
            ReleaseValue = Release(slot(IdxReleaseValue));
            ReleaseSessionOptions = Release(slot(IdxReleaseSessionOptions));
        }

        private static T Fn<T>(IntPtr pointer) where T : class
        {
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("ONNX Runtime 함수를 찾을 수 없습니다.");
            return Marshal.GetDelegateForFunctionPointer(pointer, typeof(T)) as T;
        }

        private static Action<IntPtr> Release(IntPtr pointer)
        {
            var release = Fn<ReleaseFn>(pointer);
            return handle => { if (handle != IntPtr.Zero) release(handle); };
        }

        private void Check(IntPtr status)
        {
            if (status == IntPtr.Zero) return;
            string message = Marshal.PtrToStringAnsi(getErrorMessage(status)) ?? "ONNX Runtime 오류";
            ReleaseStatus(status);
            throw new InvalidOperationException(message);
        }

        internal IntPtr CreateEnv()
        {
            IntPtr env;
            Check(createEnv(3 /* ORT_LOGGING_LEVEL_ERROR */, "EmotionCat", out env));
            try { Check(disableTelemetry(env)); }
            catch { ReleaseEnv(env); throw; }
            return env;
        }

        internal IntPtr CreateSession(IntPtr env, string modelPath, int threads)
        {
            IntPtr options;
            Check(createOptions(out options));
            try
            {
                Check(setGraphOptimization(options, 99 /* ORT_ENABLE_ALL */));
                Check(setIntraThreads(options, 1));
                Check(setInterThreads(options, 1));
                Check(setExecutionMode(options, 0 /* ORT_SEQUENTIAL: required by DirectML */));
                Check(disableMemPattern(options));
                Check(addConfig(options, "session.intra_op.allow_spinning", "0"));
                Check(addConfig(options, "session.inter_op.allow_spinning", "0"));
                Check(addConfig(options, "session.disable_cpu_ep_fallback", "1"));
                IntPtr dmlApi;
                Check(getProviderApi("DML", ApiVersion, out dmlApi));
                if (dmlApi == IntPtr.Zero) throw new InvalidOperationException("DirectML GPU 런타임이 없습니다.");
                var appendDml = Fn<AppendDmlFn>(Marshal.ReadIntPtr(dmlApi, 5 * IntPtr.Size));
                var device = new DmlDeviceOptions { Preference = 1 /* HighPerformance */, Filter = 1 /* GPU only */ };
                Check(appendDml(options, ref device));
                IntPtr session;
                Check(createSession(env, modelPath, options, out session));
                return session;
            }
            finally { ReleaseSessionOptions(options); }
        }

        internal IntPtr CreateCpuMemoryInfo()
        {
            IntPtr info;
            Check(createCpuInfo(1 /* OrtArenaAllocator */, 0 /* OrtMemTypeDefault */, out info));
            return info;
        }

        /// Wraps pinned managed memory; the caller keeps the pin alive until the value is released.
        internal IntPtr CreateTensor(IntPtr info, IntPtr data, long bytes, long[] shape, int type)
        {
            IntPtr value;
            Check(createTensor(info, data, new UIntPtr((ulong)bytes), shape, new UIntPtr((ulong)shape.Length), type, out value));
            return value;
        }

        internal IntPtr Run(IntPtr session, IntPtr[] inputNames, IntPtr[] inputs, IntPtr[] outputNames, long expiresAt)
        {
            if (expiresAt != 0 && Stopwatch.GetTimestamp() >= expiresAt) throw new OperationCanceledException();
            var outputs = new IntPtr[outputNames.Length];
            IntPtr options; Check(createRunOptions(out options));
            var gate = new object(); bool live = true, cancelled = false;
            Timer expiry = null;
            try
            {
                if (expiresAt != 0) expiry = new Timer(delegate
                {
                    lock (gate) { if (live) { cancelled = true; ReleaseStatus(terminateRun(options)); } }
                }, null, Math.Max(0, (int)Math.Min(1000, (expiresAt - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency)), Timeout.Infinite);
                IntPtr status = run(session, options, inputNames, inputs, new UIntPtr((ulong)inputs.Length),
                    outputNames, new UIntPtr((ulong)outputNames.Length), outputs);
                lock (gate)
                {
                    live = false;
                    if (cancelled || expiresAt != 0 && Stopwatch.GetTimestamp() >= expiresAt)
                    { ReleaseStatus(status); throw new OperationCanceledException(); }
                }
                Check(status);
                return outputs[0];
            }
            catch { foreach (var output in outputs) ReleaseValue(output); throw; }
            finally
            {
                if (expiry != null) expiry.Dispose();
                lock (gate) { live = false; releaseRunOptions(options); }
            }
        }

        internal IntPtr TensorData(IntPtr value)
        {
            IntPtr data;
            Check(getTensorData(value, out data));
            return data;
        }
    }
}
