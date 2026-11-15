using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace TxTools.CrossEnvIO
{
    internal sealed class AppFuncCompatibilityException : IOException
    {
        internal AppFuncCompatibilityException(string message,Exception inner=null):base(message,inner){ }
    }
    internal interface IAppFuncImage : IDisposable
    {
        string ModulePath { get; }
        string ProductVersion { get; }
        int ImageSize { get; }
        bool Is64Bit { get; }
        bool CleanupRequired { get; }
        byte[] Read(int rva,int count);
        void Write(int rva,byte[] bytes);
    }
    internal sealed class AppFuncCompatibility
    {
        internal const int PatchRva=0x27DF43;
        internal const int SyncRva=0x280110;
        // Reference metadata retained for local profile fixtures, never used as gates.
        internal const int ExpectedImageSize=0x789000;
        internal const int ExpectedFileLength=7888176;
        // Reference host build only. AppFunc's resource ProductVersion uses a
        // different format (for example 2402.1400); it is not an identity guard.
        internal const string ExpectedVersion="2402.14.0.1127";
        internal const string ProfileName="AppFunc A@0x27DF43";
        internal static readonly byte[] Original={0x0F,0x84,0xD6,0,0,0};
        internal static readonly byte[] Patched={0x90,0x90,0x90,0x90,0x90,0x90};
        internal static readonly byte[] SyncOriginal={0x40,0x55,0x53,0x56,0x57,0x41};
        private static readonly byte[] AccessContext={0xFF,0x15,0x48,0x54,0x27,0,0x83,0xF8,0xFF};
        internal static AppFuncCompatibility Current { get; set; }=new AppFuncCompatibility(AppFuncProcessImage.Open);
        private readonly Func<IAppFuncImage> _open;
        private readonly object _gate=new object();
        private bool _active;
        private string _blocked;
        internal AppFuncCompatibility(Func<IAppFuncImage> open){_open=open??throw new ArgumentNullException(nameof(open));}

        internal void EnsureIdle()
        {
            lock(_gate)
            {
                if(_blocked!=null)throw new AppFuncCompatibilityException(_blocked);
                if(_active)throw new AppFuncCompatibilityException("中文路径兼容插入正在进行，拒绝重入");
            }
        }
        internal string SupportIssue()
        {
            lock(_gate)
            {
                try{EnsureIdle();using(var image=_open())Validate(image);return null;}
                catch(Exception ex){return ex.Message;}
            }
        }
        internal T Run<T>(Func<T> operation,Action<string> log)
        {
            lock(_gate)
            {
                EnsureIdle();_active=true;
                try
                {
                    using(var image=_open())
                    {
                        Validate(image);
                        SafeLog(log,"[兼容] AppFunc "+image.ProductVersion+" 插入指令匹配；仅在本次插入期间处理 A 点，保留同步检查。");
                        T result=default;
                        Exception operationError=null,restoreError=null;
                        bool attempted=false;
                        try
                        {
                            attempted=true;
                            image.Write(PatchRva,(byte[])Patched.Clone());
                            Require(Same(image.Read(PatchRva,Original.Length),Patched),"A 点写入后回读不一致");
                            result=operation();
                        }
                        catch(Exception ex){operationError=ex;}
                        finally
                        {
                            if(attempted)
                            {
                                try
                                {
                                    byte[] current=image.Read(PatchRva,Original.Length);
                                    if(!Same(current,Original)&&!Same(current,Patched))
                                        operationError=new AppFuncCompatibilityException("A 点在插入期间出现未知指令，操作结果需核对",operationError);
                                    if(image.CleanupRequired||!Same(current,Original))
                                        image.Write(PatchRva,(byte[])Original.Clone());
                                    Require(!image.CleanupRequired&&Same(image.Read(PatchRva,Original.Length),Original),"A 点、页面保护或指令缓存未恢复");
                                    Require(Same(image.Read(SyncRva,SyncOriginal.Length),SyncOriginal),"插入期间同步检查代码发生外部变化");
                                }
                                catch(Exception ex){restoreError=ex;}
                            }
                        }
                        if(restoreError!=null)
                        {
                            _blocked="AppFunc 兼容处理恢复核验失败，已禁止本进程继续插入；请保存诊断信息并重启 PS。原因："+restoreError.Message;
                            if(operationError!=null)_blocked+="；插入错误："+operationError.Message;
                            throw new AppFuncCompatibilityException(_blocked,restoreError);
                        }
                        SafeLog(log,"[兼容] A 点原始字节、页面保护与指令缓存已恢复核验；同步检查未修改。");
                        if(operationError!=null)ExceptionDispatchInfo.Capture(operationError).Throw();
                        return result;
                    }
                }
                finally{_active=false;}
            }
        }
        private static void SafeLog(Action<string> log,string message){try{log?.Invoke(message);}catch{ }}
        internal static bool Same(byte[] left,byte[] right)=>left!=null&&right!=null&&left.SequenceEqual(right);
        private static void Require(bool condition,string message){if(!condition)throw new AppFuncCompatibilityException("中文路径兼容校验拒绝："+message);}

        private sealed class Section
        {
            internal int Rva,VirtualSize;
            internal uint Flags;
        }
        private sealed class Header
        {
            internal Section[] Sections;
        }
        private static Header HeaderOf(Func<int,int,byte[]> read)
        {
            byte[] dos=read(0,64);Require(U16(dos,0)==0x5A4D,"DOS 头不匹配");
            int pe=I32(dos,0x3C);Require(pe>=64&&pe<=0x100000,"PE 头偏移无效");
            byte[] coff=read(pe,24);Require(I32(coff,0)==0x4550&&U16(coff,4)==0x8664,"需要 AMD64 PE 镜像");
            int count=U16(coff,6),optionalSize=U16(coff,20);
            Require(count>0&&count<=96&&optionalSize>=64,"PE 头中的节表范围无效");
            byte[] optional=read(pe+24,optionalSize);
            Require(U16(optional,0)==0x20B,"需要 PE32+ 镜像");
            byte[] sections=read(pe+24+optionalSize,count*40);
            var result=new Header { Sections=new Section[count] };
            for(int i=0;i<count;i++)
            {
                int p=i*40;
                result.Sections[i]=new Section { VirtualSize=I32(sections,p+8),Rva=I32(sections,p+12),Flags=BitConverter.ToUInt32(sections,p+36) };
            }
            return result;
        }
        internal static void Validate(IAppFuncImage image)
        {
            Require(string.Equals(Path.GetFileName(image.ModulePath),"AppFunc.dll",StringComparison.OrdinalIgnoreCase),"加载模块不是 AppFunc.dll");
            Require(image.Is64Bit,"需要 64 位宿主");
            // Only the loaded instructions matter to this scoped patch. Resource
            // versions, exports, disk signatures and unrelated sections do not.
            Header memory=HeaderOf(image.Read);
            CheckBytes(image,memory,PatchRva-9,AccessContext,"文件访问判断上下文不匹配");
            CheckBytes(image,memory,PatchRva,Original,"A 点不是原始指令，拒绝叠加其他补丁");
            CheckBytes(image,memory,SyncRva,SyncOriginal,"同步检查不是原始指令");
            CheckBytes(image,memory,0x27DF4C,new byte[]{0xE8,0xBF,0x21,0,0},"同步检查调用点不匹配");
        }
        private static void CheckBytes(IAppFuncImage image,Header memory,int rva,byte[] expected,string message)
        {
            Require(rva>=0&&(long)rva+expected.Length<=image.ImageSize
                &&memory.Sections.Any(s=>s.Rva>=0&&s.VirtualSize>0&&(s.Flags&0x20000000)!=0
                    &&rva>=s.Rva&&(long)rva+expected.Length<=(long)s.Rva+s.VirtualSize),"插入指令不在已加载模块的可执行范围内");
            Require(Same(image.Read(rva,expected.Length),expected),message);
        }
        private static int I32(byte[] data,int offset)=>BitConverter.ToInt32(data,offset);
        private static ushort U16(byte[] data,int offset)=>BitConverter.ToUInt16(data,offset);
    }
}
