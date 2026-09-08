using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace Nami.Wave.Internal;

/// <summary>A parsed <c>calli</c> call-site signature, ready for <see cref="ILGenerator.EmitCalli"/>.</summary>
internal sealed class CalliSignature
{
    public required bool Unmanaged;
    public CallingConventions ManagedConvention;
    public CallingConvention UnmanagedConvention;
    public required Type ReturnType;
    public required Type[] ParameterTypes;
}

/// <summary>
/// Parses ECMA-335 method signatures (calli StandaloneSig blobs) into runtime types.
///
/// Only what a re-emitted <c>calli</c> needs: the stack shape. Custom modifiers are
/// skipped (they never change the managed stack representation), everything else is
/// resolved exactly, generic VAR/MVAR substituted from the target's own arguments.
/// Anything without a faithful managed spelling (vararg sentinels, nested function
/// pointers, generic call sites) is refused with a precise error — never guessed.
/// </summary>
internal static class CalliSignatureParser
{
    public static CalliSignature Parse(Module module, int standaloneSigToken,
        Type[] genericArgs, Type[] genericMethodArgs)
    {
        byte[] blob;
        try
        {
            blob = module.ResolveSignature(standaloneSigToken)
                ?? throw new InvalidOperationException($"calli signature token {standaloneSigToken:X8} resolved to nothing");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"cannot resolve calli signature token {standaloneSigToken:X8}", ex);
        }

        return ParseBlob(module, blob, genericArgs, genericMethodArgs);
    }

    /// <summary>Parses a raw call-site signature blob (unit-testable seam).</summary>
    public static CalliSignature ParseBlob(Module module, byte[] blob,
        Type[] genericArgs, Type[] genericMethodArgs)
    {
        var r = new SigReader(blob, module, genericArgs, genericMethodArgs);
        byte callConv = r.ReadByte();
        if ((callConv & 0x10) != 0)
        {
            throw new InvalidOperationException("Wave cannot copy generic calli (GENERIC call site)");
        }

        bool unmanaged = (callConv & 0x0F) switch
        {
            0x00 or 0x05 => false, // DEFAULT, VARARG
            0x01 or 0x02 or 0x03 or 0x04 => true, // C, STDCALL, THISCALL, FASTCALL
            // 0x09: what Roslyn emits for a bare `unmanaged` fnptr — the platform
            // default unmanaged convention. 64-bit ABIs have a single native
            // convention, so StdCall re-emits it faithfully (Wave is x64-only).
            0x09 => true,
            var c => throw new InvalidOperationException($"Wave cannot copy calli with calling convention 0x{c:X2}"),
        };

        uint paramCount = r.ReadCompressedUInt();
        var ret = r.ReadType(allowVoid: true);
        var pars = new Type[paramCount];
        for (uint i = 0; i < paramCount; i++)
        {
            pars[i] = r.ReadType(allowVoid: false);
        }
        if (!r.AtEnd)
        {
            throw new InvalidOperationException("Wave cannot copy calli with trailing signature bytes");
        }

        var sig = new CalliSignature { Unmanaged = unmanaged, ReturnType = ret, ParameterTypes = pars };
        if (unmanaged)
        {
            sig.UnmanagedConvention = (callConv & 0x0F) switch
            {
                0x01 => CallingConvention.Cdecl,
                0x02 or 0x09 => CallingConvention.StdCall,
                0x03 => CallingConvention.ThisCall,
                0x04 => CallingConvention.FastCall,
                _ => throw new InvalidOperationException("unreachable"),
            };
        }
        else
        {
            var cconv = (callConv & 0x0F) == 0x05 ? CallingConventions.VarArgs : CallingConventions.Standard;
            if ((callConv & 0x20) != 0)
            {
                cconv |= CallingConventions.HasThis;
            }
            if ((callConv & 0x40) != 0)
            {
                cconv |= CallingConventions.ExplicitThis;
            }
            sig.ManagedConvention = cconv;
        }
        return sig;
    }

    /// <summary>Cursor over a signature blob with ECMA-335 compressed-int decoding.</summary>
    private sealed class SigReader(byte[] blob, Module module, Type[] genericArgs, Type[] genericMethodArgs)
    {
        private int _pos;

        public bool AtEnd => _pos >= blob.Length;

        public byte ReadByte()
        {
            if (_pos >= blob.Length)
            {
                throw new InvalidOperationException("truncated calli signature");
            }
            return blob[_pos++];
        }

        public uint ReadCompressedUInt()
        {
            byte b0 = ReadByte();
            if ((b0 & 0x80) == 0)
            {
                return b0;
            }
            if ((b0 & 0x40) == 0)
            {
                return (uint)(((b0 & 0x3F) << 8) | ReadByte());
            }
            return (uint)(((b0 & 0x1F) << 24) | (ReadByte() << 16) | (ReadByte() << 8) | ReadByte());
        }

        public Type ReadType(bool allowVoid)
        {
            // Prefix markers that don't change the stack shape.
            for (; ; )
            {
                byte marker = ReadByte();
                if (marker is 0x1E or 0x1F) // CMOD_REQD / CMOD_OPTD
                {
                    ReadCompressedUInt(); // coded TypeDefOrRefOrSpec token, skipped
                    continue;
                }
                if (marker == 0x45) // PINNED (never in a sig, tolerate)
                {
                    continue;
                }
                return ReadElement(marker, allowVoid);
            }
        }

        private Type ReadElement(byte etype, bool allowVoid)
        {
            switch (etype)
            {
                case 0x01: // VOID
                    if (allowVoid)
                    {
                        return typeof(void);
                    }
                    throw new InvalidOperationException("Wave cannot copy calli with void parameter");
                case 0x02: return typeof(bool);
                case 0x03: return typeof(char);
                case 0x04: return typeof(sbyte);
                case 0x05: return typeof(byte);
                case 0x06: return typeof(short);
                case 0x07: return typeof(ushort);
                case 0x08: return typeof(int);
                case 0x09: return typeof(uint);
                case 0x0A: return typeof(long);
                case 0x0B: return typeof(ulong);
                case 0x0C: return typeof(float);
                case 0x0D: return typeof(double);
                case 0x0E: return typeof(string);
                case 0x16: return typeof(TypedReference); // TYPEDBYREF
                case 0x18: return typeof(IntPtr); // I
                case 0x19: return typeof(UIntPtr); // U
                case 0x1B: return typeof(object); // OBJECT
                case 0x0F: return ReadType(allowVoid: false).MakePointerType(); // PTR
                case 0x10: return ReadType(allowVoid: false).MakeByRefType(); // BYREF
                case 0x1C: return ReadType(allowVoid: false).MakeArrayType(); // SZARRAY
                case 0x11: // VALUETYPE
                case 0x12: // CLASS
                    return ResolveCodedToken();
                case 0x13: // VAR (generic type parameter)
                {
                    uint n = ReadCompressedUInt();
                    if (n >= (uint)genericArgs.Length)
                    {
                        throw new InvalidOperationException($"Wave cannot copy calli with unbound generic parameter !{n}");
                    }
                    return genericArgs[n];
                }
                case 0x1D: // MVAR (generic method parameter)
                {
                    uint n = ReadCompressedUInt();
                    if (n >= (uint)genericMethodArgs.Length)
                    {
                        throw new InvalidOperationException($"Wave cannot copy calli with unbound generic method parameter !!{n}");
                    }
                    return genericMethodArgs[n];
                }
                case 0x14: // ARRAY: elem, rank, sizes..., lobounds...
                {
                    var elem = ReadType(allowVoid: false);
                    uint rank = ReadCompressedUInt();
                    uint numSizes = ReadCompressedUInt();
                    for (uint i = 0; i < numSizes; i++)
                    {
                        ReadCompressedUInt();
                    }
                    uint numBounds = ReadCompressedUInt();
                    for (uint i = 0; i < numBounds; i++)
                    {
                        ReadCompressedUInt();
                    }
                    return elem.MakeArrayType((int)rank);
                }
                case 0x15: // GENERICINST: CLASS/VALUETYPE token, arg count, args
                {
                    byte kind = ReadByte();
                    if (kind is not (0x11 or 0x12))
                    {
                        throw new InvalidOperationException($"Wave cannot copy calli with generic-inst kind 0x{kind:X2}");
                    }
                    var def = ResolveCodedToken();
                    uint nargs = ReadCompressedUInt();
                    var args = new Type[nargs];
                    for (uint i = 0; i < nargs; i++)
                    {
                        args[i] = ReadType(allowVoid: false);
                    }
                    try
                    {
                        return def.MakeGenericType(args);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Wave cannot close generic calli type {def}", ex);
                    }
                }
                case 0x1A: // FNPTR
                    throw new InvalidOperationException("Wave cannot copy calli with a nested function-pointer signature");
                case 0x41: // SENTINEL (vararg)
                    throw new InvalidOperationException("Wave cannot copy vararg calli (SENTINEL)");
                default:
                    throw new InvalidOperationException($"Wave cannot copy calli with element type 0x{etype:X2}");
            }
        }

        private Type ResolveCodedToken()
        {
            // TypeDefOrRefOrSpec coded index: low 2 bits select the table.
            uint coded = ReadCompressedUInt();
            uint token = (coded & 3) switch
            {
                0 => 0x02000000u | (coded >> 2), // TypeDef
                1 => 0x01000000u | (coded >> 2), // TypeRef
                2 => 0x1B000000u | (coded >> 2), // TypeSpec
                _ => throw new InvalidOperationException("Wave cannot copy calli with an invalid type token"),
            };
            try
            {
                return module.ResolveType((int)token, genericArgs, genericMethodArgs)
                    ?? throw new InvalidOperationException($"calli type token {token:X8} resolved to nothing");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException($"cannot resolve calli type token {token:X8}", ex);
            }
        }
    }
}
