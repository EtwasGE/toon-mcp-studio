using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Toon.Infrastructure;

// A kernel job closes when worker dies, including TerminateProcess. Its children die with it.
internal static class ProcessTree
{
    public static IDisposable? Attach(Process process) {
        if(!OperatingSystem.IsWindows())return null;
        var handle=CreateJobObject(IntPtr.Zero,null);if(handle.IsInvalid)throw new Win32Exception();
        var info=new ExtendedLimit { Basic=new BasicLimit { LimitFlags=0x2000 } };var length=Marshal.SizeOf<ExtendedLimit>();var ptr=Marshal.AllocHGlobal(length);
        try{Marshal.StructureToPtr(info,ptr,false);if(!SetInformationJobObject(handle,9,ptr,(uint)length)||!AssignProcessToJobObject(handle,process.Handle)){handle.Dispose();throw new Win32Exception();}}finally{Marshal.FreeHGlobal(ptr);}return handle;
    }
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimit {public long PerProcessUserTimeLimit,PerJobUserTimeLimit;public uint LimitFlags;public UIntPtr MinimumWorkingSetSize,MaximumWorkingSetSize;public uint ActiveProcessLimit;public UIntPtr Affinity;public uint PriorityClass,SchedulingClass;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters {public ulong ReadOperationCount,WriteOperationCount,OtherOperationCount,ReadTransferCount,WriteTransferCount,OtherTransferCount;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimit {public BasicLimit Basic;public IoCounters Io;public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetInformationJobObject(SafeFileHandle job,int infoClass,IntPtr info,uint length);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,IntPtr process);
}
