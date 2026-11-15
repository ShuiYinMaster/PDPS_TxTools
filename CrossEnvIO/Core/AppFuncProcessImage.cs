using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace TxTools.CrossEnvIO
{
    internal sealed class AppFuncProcessImage : IAppFuncImage
    {
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool GetModuleHandleEx(uint flags,IntPtr address,out IntPtr module);
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool FreeLibrary(IntPtr module);
        private IntPtr _module;
        private readonly NativeCodeMemory _memory;
        public string ModulePath { get; }
        public string ProductVersion { get; }
        public int ImageSize { get; }
        public bool Is64Bit=>Environment.Is64BitProcess;
        public bool CleanupRequired=>_memory.CleanupRequired;

        private AppFuncProcessImage(ProcessModule module)
        {
            ModulePath=module.FileName;ImageSize=module.ModuleMemorySize;
            if(!GetModuleHandleEx(4,module.BaseAddress,out _module))throw Failure("无法保持已加载 AppFunc 模块有效");
            try
            {
                if(_module!=module.BaseAddress)throw new AppFuncCompatibilityException("加载模块地址发生变化，未修改内存");
                try{ProductVersion=FileVersionInfo.GetVersionInfo(ModulePath).ProductVersion;}
                catch{ProductVersion="未提供";} // Diagnostic only; never block insertion on file metadata.
                _memory=new NativeCodeMemory(_module,ImageSize);
            }
            catch{Dispose();throw;}
        }
        internal static IAppFuncImage Open()
        {
            if(!Environment.Is64BitProcess)throw new AppFuncCompatibilityException("中文路径兼容需要 64 位 PS 宿主");
            using(var process=Process.GetCurrentProcess())
            {
                var modules=process.Modules.Cast<ProcessModule>().Where(m=>string.Equals(m.ModuleName,"AppFunc.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
                if(modules.Length!=1)throw new AppFuncCompatibilityException("需要唯一的已加载 AppFunc.dll，实际数量："+modules.Length+"；未修改内存");
                return new AppFuncProcessImage(modules[0]);
            }
        }
        public byte[] Read(int rva,int count)=>_memory.Read(rva,count);
        public void Write(int rva,byte[] bytes)
        {
            if(rva!=AppFuncCompatibility.PatchRva||bytes==null||bytes.Length!=6)
                throw new AppFuncCompatibilityException("拒绝写入 A 点以外的 AppFunc 指令");
            if(!AppFuncCompatibility.Same(bytes,AppFuncCompatibility.Original)&&!AppFuncCompatibility.Same(bytes,AppFuncCompatibility.Patched))
                throw new AppFuncCompatibilityException("拒绝写入未定义的 A 点指令");
            _memory.Write(rva,bytes);
        }
        public void Dispose()
        {
            if(_module==IntPtr.Zero)return;
            IntPtr module=_module;_module=IntPtr.Zero;
            if(!FreeLibrary(module))throw Failure("无法释放本次 AppFunc 模块引用");
        }
        private static AppFuncCompatibilityException Failure(string message)=>new AppFuncCompatibilityException(message+"："+new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    // This writer only accesses the caller-owned current-process address range.
    // Keep the first page protection until both byte and protection restoration succeed.
    internal sealed class NativeCodeMemory
    {
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool VirtualProtect(IntPtr address,UIntPtr size,uint protection,out uint oldProtection);
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool FlushInstructionCache(IntPtr process,IntPtr address,UIntPtr size);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        private readonly IntPtr _base;
        private readonly int _size;
        private uint? _pendingProtection;
        private int _pendingRva,_pendingLength;
        private bool _needsFlush;
        internal bool CleanupRequired=>_pendingProtection.HasValue||_needsFlush;
        internal NativeCodeMemory(IntPtr address,int size){if(address==IntPtr.Zero||size<=0)throw new ArgumentException("无效内存范围");_base=address;_size=size;}
        private IntPtr Address(int rva,int count)
        {
            if(rva<0||count<0||(long)rva+count>_size)throw new AppFuncCompatibilityException("AppFunc 内存读取范围无效");
            return IntPtr.Add(_base,rva);
        }
        internal byte[] Read(int rva,int count){var data=new byte[count];Marshal.Copy(Address(rva,count),data,0,count);return data;}
        internal void Write(int rva,byte[] bytes)
        {
            if(bytes==null||bytes.Length==0)throw new ArgumentException("写入内容为空");
            IntPtr address=Address(rva,bytes.Length);
            if(_pendingProtection.HasValue&&(_pendingRva!=rva||_pendingLength!=bytes.Length))throw new AppFuncCompatibilityException("尚有未恢复的页面保护");
            uint old;
            if(!VirtualProtect(address,(UIntPtr)bytes.Length,0x40,out old))throw Error("VirtualProtect(RW) 失败");
            if(!_pendingProtection.HasValue){_pendingProtection=old;_pendingRva=rva;_pendingLength=bytes.Length;}
            Exception copyError=null;string protectError=null,flushError=null;
            try{_needsFlush=true;Marshal.Copy(bytes,0,address,bytes.Length);}
            catch(Exception ex){copyError=ex;}
            finally
            {
                uint ignored;
                if(VirtualProtect(address,(UIntPtr)bytes.Length,_pendingProtection.Value,out ignored))_pendingProtection=null;
                else protectError=Error("恢复页面保护失败").Message;
                if(FlushInstructionCache(GetCurrentProcess(),address,(UIntPtr)bytes.Length))_needsFlush=false;
                else flushError=Error("FlushInstructionCache 失败").Message;
            }
            if(copyError!=null||protectError!=null||flushError!=null)
                throw new AppFuncCompatibilityException(string.Join("；",new[]{copyError?.Message,protectError,flushError}.Where(s=>s!=null)),copyError);
        }
        private static AppFuncCompatibilityException Error(string message)=>new AppFuncCompatibilityException(message+"："+new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }
}
