using EasyHook;
using Elements.Core;
using FrooxEngine;
using ResoniteModLoader;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Contexts;
using System.Text;
using System.Threading;

namespace ResoniteNativeVarjoIntegration;

class VarjoNativeInterface
{
    private IntPtr _session;
    protected GazeData gazeData;
    protected EyeMeasurements eyeMeasurements;

    // windows consts because I'm too lazy to find references
    private const int GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004;
    private const int GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002;



    #region Lifetime methods (Init, Update, Teardown)
    public bool Initialize()
    {
        if (!VarjoAvailable())
        {
            ResoniteMod.Error("Varjo headset isn't detected");
            return false;
        }
        bool libraryLoaded = false;
        using (var hook = EasyHook.LocalHook.Create(EasyHook.LocalHook.GetProcAddress("kernel32.dll", "GetCurrentProcessId"), new GetCurrentProcessIdDelegate(GetCurrentProcessId_Hook), this))
        {
            hook.ThreadACL.SetExclusiveACL(new Int32[] { });
            // Call the evil Varjoe code with thw evil fake processId hook
            libraryLoaded = LoadLibrary();

            if (libraryLoaded)
            {
                _session = varjo_SessionInit();
                // Aand we don't need the evil hook anymore
                hook.ThreadACL.SetInclusiveACL(new Int32[] { });

                if (_session == IntPtr.Zero)
                {
                    ResoniteMod.Warn("Varjo init failed");
                    return false;
                }
                if (!varjo_IsGazeAllowed(_session))
                {
                    ResoniteMod.Error("Gaze tracking is not allowed! Please enable it in the Varjo Base!");
                    return false;
                }
                varjo_GazeInit(_session);
                varjo_SyncProperties(_session);
                return true;
            }
            // Aand we don't need the evil hook anymore either
            hook.ThreadACL.SetInclusiveACL(new Int32[] { });
        }
        return false;
    }

    public void Teardown()
    {
        varjo_SessionShutDown(_session);
    }

    // Get's the newest Data from the SDK and stores it internally
    public bool Update()
    {
        if (_session == IntPtr.Zero)
            return false;

        // Gets GazeData and EyeMeasurements from the Varjo SDK
        // Return value states whether or not the request was successful (true = has Data; false = Error occured)
        bool hasData = varjo_GetGazeData(_session, out gazeData, out eyeMeasurements);

        if (!hasData)
            ResoniteMod.Warn("Error while getting Gaze Data");

        return hasData;
    }
    #endregion

    #region Public Getters
    public GazeData GetGazeData()
    {
        return gazeData;
    }

    public EyeMeasurements GetEyeMeasurements()
    {
        return eyeMeasurements;
    }

    public string GetHMDName()
    {
        int bufferSize = varjo_GetPropertyStringSize(_session, VarjoPropertyKey.HMDProductName);
        StringBuilder buffer = new StringBuilder(bufferSize);
        varjo_GetPropertyString(_session, VarjoPropertyKey.HMDProductName, buffer, bufferSize);

        return buffer.ToString();
    }
    #endregion

    #region Internal helper methods
    private bool LoadLibrary()
    {
        var path = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) + "\\TrackingLibs\\VarjoLib.dll";
        if (path == null)
        {
            ResoniteMod.Error(string.Concat("Couldn't extract the library ", path));
            return false;
        }
        if (LoadLibrary(path) == IntPtr.Zero)
        {
            ResoniteMod.Error(string.Concat("Unable to load library ", path));
            return false;
        }
        ResoniteMod.Msg(string.Concat("Loaded library ", path));
        return true;
    }

    private static bool VarjoAvailable()
    {
        // totally not how the official Varjo library works under the hood
        return File.Exists("\\\\.\\pipe\\Varjo\\InfoService");
    }


    //Our actual hooked handler function
    UInt32 GetCurrentProcessId_Hook()
    {
        // We try to only intercept calls from the Varjo client.
        // We can't have a _ReturnAddress() intrinsic in C# so we'll use the EasyHook's method. I promise it'll only be called a few times
        IntPtr returnAddr = HookRuntimeInfo.ReturnAddress;
        IntPtr callerModule;
        if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, returnAddr, out callerModule))
        {
            IntPtr libVarjo = GetModuleHandleA("VarjoLib.dll");
            IntPtr libVarjoRuntime = GetModuleHandleA("VarjoRuntime.dll");
            if (callerModule == libVarjo || callerModule == libVarjoRuntime)
            {
                // return bogus process id
                ResoniteMod.Warn("Hijacked Varjo's process ID call! All good");
                return GetCurrentProcessId() + 42;
            }
        }
        return GetCurrentProcessId();
    }


    #endregion

    #region DllImports
    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = false, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern bool varjo_IsAvailable();

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern IntPtr varjo_SessionInit();

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern void varjo_SessionShutDown(IntPtr session);

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern void varjo_GazeInit(IntPtr session);

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern int varjo_GetError(IntPtr session);

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern string varjo_GetErrorDesc(int errorCode);

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern bool varjo_IsGazeAllowed(IntPtr session);

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern bool varjo_IsGazeCalibrated(IntPtr session);

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern GazeData varjo_GetGaze(IntPtr session);

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern bool varjo_GetGazeData(IntPtr session, out GazeData gaze, out EyeMeasurements eyeMeasurements);

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern void varjo_RequestGazeCalibration(IntPtr session);

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern bool varjo_GetPropertyBool(IntPtr session, VarjoPropertyKey propertyKey);

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern int varjo_GetPropertyInt(IntPtr session, VarjoPropertyKey propertyKey);

    [DllImport("VarjoLib", CharSet = CharSet.Ansi)]
    private static extern void varjo_GetPropertyString(IntPtr session, VarjoPropertyKey propertyKey, StringBuilder buffer, int bufferSize);

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern int varjo_GetPropertyStringSize(IntPtr session, VarjoPropertyKey propertyKey);

    [DllImport("VarjoLib", CharSet = CharSet.Auto)]
    private static extern void varjo_SyncProperties(IntPtr session);

    #endregion

    #region Hooking related imports and structs

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern UInt32 GetCurrentProcessId();

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    delegate UInt32 GetCurrentProcessIdDelegate();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll")]
    static extern bool GetModuleHandleExA(int dwFlags, IntPtr ModuleName, out IntPtr phModule);

    #endregion

}
