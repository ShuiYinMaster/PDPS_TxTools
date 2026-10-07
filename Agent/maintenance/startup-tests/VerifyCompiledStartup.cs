using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
class VerifyCompiledStartup
{
    static readonly Dictionary<ushort, OpCode> Codes = typeof(OpCodes).GetFields().Where(f=>f.FieldType==typeof(OpCode))
        .Select(f=>(OpCode)f.GetValue(null)).ToDictionary(op=>unchecked((ushort)op.Value));
    static string TypeName(MetadataReader reader, EntityHandle handle) => handle.Kind switch {
        HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Name),
        HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
        _ => handle.Kind.ToString()
    };
    static List<string> Calls(PEReader pe, MetadataReader reader, MethodDefinition method) {
        var calls=new List<string>();var il=pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
        for(int i=0;i<il.Length;) {
            ushort code=il[i++];if(code==0xfe)code=(ushort)(0xfe00|il[i++]);var op=Codes[code];
            if(op.OperandType==OperandType.InlineMethod){var handle=MetadataTokens.EntityHandle(BitConverter.ToInt32(il,i));
                if(handle.Kind==HandleKind.MemberReference){var m=reader.GetMemberReference((MemberReferenceHandle)handle);calls.Add(TypeName(reader,m.Parent)+"."+reader.GetString(m.Name));}
                if(handle.Kind==HandleKind.MethodDefinition){var m=reader.GetMethodDefinition((MethodDefinitionHandle)handle);calls.Add(TypeName(reader,m.GetDeclaringType())+"."+reader.GetString(m.Name));}
            }
            i+=op.OperandType switch {
                OperandType.InlineNone=>0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,
                OperandType.InlineVar=>2,
                OperandType.InlineI8 or OperandType.InlineR=>8,
                OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(il,i),
                _=>4
            };
        }return calls;
    }
    static int Main(string[] args){using var stream=File.OpenRead(args[0]);using var pe=new PEReader(stream);var reader=pe.GetMetadataReader();
        var command=reader.TypeDefinitions.Select(h=>reader.GetTypeDefinition(h)).Single(t=>reader.GetString(t.Name)=="TxAgentCommand");
        var initializer=command.GetMethods().Select(h=>reader.GetMethodDefinition(h)).Single(m=>reader.GetString(m.Name)==".cctor");
        var calls=Calls(pe,reader,initializer);string[] heavy={"RecipeStore","BuildToolRegistry","InitializePet","UserPrefsStore","TxAgentService","WaitOne","Process.Start"};
        if(calls.Any(c=>heavy.Any(c.Contains)))throw new Exception("Heavy registration work: "+string.Join(",",calls));
        Console.WriteLine("PASS: actual DLL registration initializer only calls "+string.Join(", ",calls));
        var startup=command.GetMethods().Select(h=>reader.GetMethodDefinition(h)).Single(m=>reader.GetString(m.Name)=="StartBackgroundServices");
        var startupCalls=Calls(pe,reader,startup);if(!startupCalls.Any(c=>c=="Task.Run"))throw new Exception("Startup does not dispatch background work");
        var actions=reader.TypeDefinitions.Select(h=>reader.GetTypeDefinition(h)).Single(t=>reader.GetString(t.Name)=="RecipeUiActions");
        var list=actions.GetMethods().Select(h=>reader.GetMethodDefinition(h)).Single(m=>reader.GetString(m.Name)=="List");
        if(Calls(pe,reader,list).Any(c=>c.Contains("SceneObjects")||c.Contains("GetAllDescendants")))throw new Exception("Recipe list scans scene");
        Console.WriteLine("PASS: compiled startup dispatches background work; recipe list never calls scene traversal");
        Console.WriteLine("PASS: PE metadata inspected without loading SDK assemblies or running plugin code");return 0;
    }
}
