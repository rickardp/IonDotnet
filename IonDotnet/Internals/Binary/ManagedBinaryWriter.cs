﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using IonDotnet.Utils;

namespace IonDotnet.Internals.Binary
{
    internal sealed class ManagedBinaryWriter : IIonWriter
    {
        private sealed class PagedWriter256Buffer : PagedWriterBuffer
        {
            public PagedWriter256Buffer() : base(512)
            {
            }
        }

        private enum SymbolState
        {
            SystemSymbols,
            LocalSymbolsWithImportsOnly,
            LocalSymbols,
            LocalSymbolsFlushed
        }

        private class ImportedSymbolsContext
        {
            private readonly Dictionary<string, int> _dict = new Dictionary<string, int>();

            public readonly ISymbolTable[] Parents;
            public readonly int LocalSidStart;

            public ImportedSymbolsContext(ISymbolTable[] imports)
            {
                Parents = imports;
                //add all the system symbols
//                foreach (var systemSymbolToken in Symbols.SystemSymbolTokens)
//                {
//                    _dict.Add(systemSymbolToken.Text, systemSymbolToken.Sid);
//                }

                LocalSidStart = SystemSymbols.Ion10MaxId + 1;
                foreach (var symbolTable in imports)
                {
                    if (symbolTable.IsShared) throw new IonException("Import table cannot be shared");
                    if (symbolTable.IsSystem) continue;

                    var declaredSymbols = symbolTable.IterateDeclaredSymbolNames();
                    while (declaredSymbols.HasNext())
                    {
                        var text = declaredSymbols.Next();
                        if (text != null)
                        {
                            _dict.TryAdd(text, LocalSidStart);
                        }

                        LocalSidStart++;
                    }
                }
            }

            public bool TryGetValue(string text, out int val)
            {
                val = default;

                if (text == null) return false;
                var systemTab = SharedSymbolTable.GetSystem(1);
                var st = systemTab.Find(text);
                if (st.Text != null)
                {
                    //found it
                    val = st.Sid;
                    return true;
                }

//                for (int i = 0, l = Symbols.SystemSymbolTokens.Length; i < l; i++)
//                {
//                    var systemToken = Symbols.SystemSymbolTokens[i];
//                    if (systemToken.Text != text) continue;
//                    val = systemToken.Sid;
//                    return true;
//                }

                if (Parents.Length == 0) return false;

                return _dict.TryGetValue(text, out val);
            }
        }

        private readonly IDictionary<string, int> _locals;
        private bool _localsLocked;

        private readonly RawBinaryWriter _symbolsWriter;
        private readonly RawBinaryWriter _userWriter;
        private LocalSymbolTableView _localSymbolTableView;
        private readonly ImportedSymbolsContext _importContext;
        private SymbolState _symbolState;

        public ManagedBinaryWriter(ISymbolTable[] importedTables)
        {
            //raw writers and their buffers
            var lengthWriterBuffer = new PagedWriter256Buffer();
            var lengthSegment = new List<Memory<byte>>(2);
            _symbolsWriter = new RawBinaryWriter(lengthWriterBuffer, new PagedWriter256Buffer(), lengthSegment);
            _userWriter = new RawBinaryWriter(lengthWriterBuffer, new PagedWriter256Buffer(), lengthSegment);

            _importContext = new ImportedSymbolsContext(importedTables);
            _locals = new Dictionary<string, int>();
        }

        /// <summary>
        /// Only runs if the symbol state is SystemSymbol. Basically this will write the version marker,
        /// write all imported table names, and move to the local symbols
        /// </summary>
        /// <param name="writeIvm">Whether to write the Ion version marker</param>
        private void StartLocalSymbolTableIfNeeded(bool writeIvm)
        {
            if (_symbolState != SymbolState.SystemSymbols) return;

            if (writeIvm)
            {
                _symbolsWriter.WriteIonVersionMarker();
            }

            _symbolsWriter.AddTypeAnnotationSymbol(Symbols.GetSystemSymbol(SystemSymbols.IonSymbolTableSid));

            _symbolsWriter.StepIn(IonType.Struct); // $ion_symbol_table:{}
            if (_importContext.Parents.Length > 0)
            {
                _symbolsWriter.SetFieldNameSymbol(Symbols.GetSystemSymbol(SystemSymbols.ImportsSid));
                _symbolsWriter.StepIn(IonType.List); // $imports: []

                foreach (var importedTable in _importContext.Parents)
                {
                    _symbolsWriter.StepIn(IonType.Struct); // {name:'a', version: 1, max_id: 33}
                    _symbolsWriter.SetFieldNameSymbol(Symbols.GetSystemSymbol(SystemSymbols.NameSid));
                    _symbolsWriter.WriteString(importedTable.Name);
                    _symbolsWriter.SetFieldNameSymbol(Symbols.GetSystemSymbol(SystemSymbols.VersionSid));
                    _symbolsWriter.WriteInt(importedTable.Version);
                    _symbolsWriter.SetFieldNameSymbol(Symbols.GetSystemSymbol(SystemSymbols.MaxIdSid));
                    _symbolsWriter.WriteInt(importedTable.MaxId);
                    _symbolsWriter.StepOut();
                }

                _symbolsWriter.StepOut(); // $imports: []
            }

            _symbolState = SymbolState.LocalSymbolsWithImportsOnly;
        }

        /// <summary>
        /// Only run if symbolState is LocalSymbolsWithImportsOnly. This will start the list of local symbols
        /// </summary>
        private void StartLocalSymbolListIfNeeded()
        {
            if (_symbolState != SymbolState.LocalSymbolsWithImportsOnly) return;

            _symbolsWriter.SetFieldNameSymbol(Symbols.GetSystemSymbol(SystemSymbols.SymbolsSid));
            _symbolsWriter.StepIn(IonType.List); // symbols: []
            _symbolState = SymbolState.LocalSymbols;
        }

        /// <summary>
        /// Try intern a text into the symbols list, if the text is not in there already
        /// </summary>
        /// <param name="text">Text to intern</param>
        /// <returns>Corresponding token</returns>
        private SymbolToken Intern(string text)
        {
            Debug.Assert(text != null);

            var foundInImported = _importContext.TryGetValue(text, out var tokenSid);
            if (foundInImported)
            {
                if (tokenSid > SystemSymbols.Ion10MaxId)
                {
                    StartLocalSymbolTableIfNeeded(true);
                }

                return new SymbolToken(text, tokenSid);
            }

            //try the locals
            var foundInLocal = _locals.TryGetValue(text, out tokenSid);
            if (foundInLocal) return new SymbolToken(text, tokenSid);

            //try adding the text to the locals
            if (_localsLocked) throw new IonException("Local table is made read-only");

            StartLocalSymbolTableIfNeeded(true);
            StartLocalSymbolListIfNeeded();

            //progressively set the new sid
            tokenSid = _importContext.LocalSidStart + _locals.Count;
            _locals.Add(text, tokenSid);

            //write the new symbol to the list
            _symbolsWriter.WriteString(text);

            return new SymbolToken(text, tokenSid);
        }

        /// <summary>
        /// This basically interns the text of the token and return the new token
        /// </summary>
        private SymbolToken InternSymbol(SymbolToken token)
        {
            if (token == default || token.Text == null) return default;
            return Intern(token.Text);
        }

        public ISymbolTable SymbolTable => _localSymbolTableView ?? (_localSymbolTableView = new LocalSymbolTableView(this));

        /// <inheritdoc />
        /// <summary>
        /// This is supposed to close the writer and release all their resources
        /// </summary>
        public void Dispose()
        {
            //first try to flush things out
//            Flush();

            var lengthBuffer = _userWriter?.GetLengthBuffer();
            Debug.Assert(lengthBuffer == _symbolsWriter.GetLengthBuffer());
            lengthBuffer?.Dispose();

            _userWriter?.GetDataBuffer().Dispose();
            _symbolsWriter?.GetDataBuffer().Dispose();
        }

        public void Flush(Stream outputStream)
        {
            if (!PrepareFlush())
                return;

            _symbolsWriter.PrepareFlush();
            _symbolsWriter.Flush(outputStream);

            _userWriter.PrepareFlush();
            _userWriter.Flush(outputStream);
            
            Finish();
        }

        public void Flush(ref byte[] bytes)
        {
            if (!PrepareFlush())
                return;

            var sLength = _symbolsWriter.PrepareFlush();
            var uLength = _userWriter.PrepareFlush();
            var tLength = sLength + uLength;
            if (bytes == null || bytes.Length < tLength)
            {
                bytes = new byte[tLength];
            }

            _symbolsWriter.Flush(bytes);
            _userWriter.Flush(new Memory<byte>(bytes, sLength, uLength));
            
            Finish();
        }

        public int Flush(Memory<byte> buffer)
        {
            if (!PrepareFlush())
                return 0;

            var sLength = _symbolsWriter.PrepareFlush();
            var uLength = _userWriter.PrepareFlush();
            var tLength = sLength + uLength;
            if (buffer.Length < tLength)
                return 0;

            _symbolsWriter.Flush(buffer);
            _userWriter.Flush(buffer.Slice(sLength, uLength));
            Finish();
            return tLength;
        }

        private bool PrepareFlush()
        {
            if (_userWriter.GetDepth() != 0)
                return false;

            switch (_symbolState)
            {
                case SymbolState.SystemSymbols:
                    _symbolsWriter.WriteIonVersionMarker();
                    break;
                case SymbolState.LocalSymbolsWithImportsOnly:
                    _symbolsWriter.StepOut();
                    break;
                case SymbolState.LocalSymbols:
                    _symbolsWriter.StepOut();
                    _symbolsWriter.StepOut();
                    break;
                case SymbolState.LocalSymbolsFlushed:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            
            _symbolState = SymbolState.LocalSymbolsFlushed;

            return true;
        }

        public void Finish()
        {
            if (_userWriter.GetDepth() != 0) throw new IonException($"Cannot finish writing at depth {_userWriter.GetDepth()}");

            _symbolsWriter.Finish();
            _userWriter.Finish();

            //reset local symbols
            _locals.Clear();
            _localsLocked = false;
            _symbolState = SymbolState.SystemSymbols;
        }

        public void SetFieldName(string name)
        {
            if (!IsInStruct) throw new IonException("Cannot set a field name if the current container is not struct");
            if (name == null) throw new ArgumentNullException(nameof(name));

            var token = Intern(name);
            _userWriter.SetFieldNameSymbol(token);
        }

        public void SetFieldNameSymbol(SymbolToken name)
        {
            var token = InternSymbol(name);
            _userWriter.SetFieldNameSymbol(token);
        }

        public void StepIn(IonType type)
        {
            // TODO implement top-level symbol table
            _userWriter.StepIn(type);
        }

        public void StepOut()
        {
            // TODO implement top-level symbol table
            _userWriter.StepOut();
        }

        public bool IsInStruct => _userWriter.IsInStruct;

        public void WriteValue(IIonReader reader)
        {
            throw new NotImplementedException();
        }

        public void WriteValues(IIonReader reader)
        {
            throw new NotImplementedException();
        }

        public void WriteNull()
        {
            _userWriter.WriteNull();
        }

        public void WriteNull(IonType type)
        {
            _userWriter.WriteNull(type);
        }

        public void WriteBool(bool value)
        {
            _userWriter.WriteBool(value);
        }

        public void WriteInt(long value)
        {
            _userWriter.WriteInt(value);
        }

        public void WriteInt(BigInteger value)
        {
            _userWriter.WriteInt(value);
        }

        public void WriteFloat(double value)
        {
            _userWriter.WriteFloat(value);
        }

        public void WriteDecimal(decimal value)
        {
            _userWriter.WriteDecimal(value);
        }

        public void WriteTimestamp(Timestamp value)
        {
            _userWriter.WriteTimestamp(value);
        }

        public void WriteSymbol(string symbol)
        {
            var token = Intern(symbol);
            _userWriter.WriteSymbolToken(token);
        }

        public void WriteString(string value)
        {
            _userWriter.WriteString(value);
        }

        public void WriteBlob(ReadOnlySpan<byte> value) => _userWriter.WriteBlob(value);

        public void WriteClob(ReadOnlySpan<byte> value) => _userWriter.WriteClob(value);

        public void SetTypeAnnotation(string annotation)
        {
            if (annotation == default) throw new ArgumentNullException(nameof(annotation));

            _userWriter.ClearAnnotations();
            var token = Intern(annotation);
            _userWriter.AddTypeAnnotationSymbol(token);
        }

        public void SetTypeAnnotationSymbols(IEnumerable<SymbolToken> annotations)
        {
            if (annotations == null) throw new ArgumentNullException(nameof(annotations));
            foreach (var annotation in annotations)
            {
                var token = InternSymbol(annotation);
                _userWriter.AddTypeAnnotationSymbol(token);
            }
        }

        public void AddTypeAnnotation(string annotation)
        {
            var token = Intern(annotation);
            _userWriter.AddTypeAnnotationSymbol(token);
        }

        /// <summary>
        /// Reflects the 'view' of the local symbol used in this writer
        /// </summary>
        private class LocalSymbolTableView : AbstractSymbolTable
        {
            private readonly ManagedBinaryWriter _writer;

            public LocalSymbolTableView(ManagedBinaryWriter writer) : base(string.Empty, 0)
            {
                _writer = writer;
            }

            public override bool IsLocal => true;
            public override bool IsShared => false;
            public override bool IsSubstitute => false;
            public override bool IsSystem => false;
            public override bool IsReadOnly => _writer._localsLocked;

            public override void MakeReadOnly() => _writer._localsLocked = true;

            public override ISymbolTable GetSystemTable() => SharedSymbolTable.GetSystem(1);

            public override IEnumerable<ISymbolTable> GetImportedTables() => _writer._importContext.Parents;

            public override int GetImportedMaxId() => _writer._importContext.LocalSidStart - 1;

            public override int MaxId => GetImportedMaxId() + _writer._locals.Count;

            public override SymbolToken Intern(string text)
            {
                var existing = Find(text);
                if (existing != default) return existing;
                if (IsReadOnly) throw new ReadOnlyException("Table is read-only");

                return _writer.Intern(text);
            }

            public override SymbolToken Find(string text)
            {
                if (text == null) throw new ArgumentNullException(nameof(text));

                var found = _writer._importContext.TryGetValue(text, out var sid);
                if (found) return new SymbolToken(text, sid);
                found = _writer._locals.TryGetValue(text, out sid);

                return found ? new SymbolToken(text, sid) : default;
            }

            public override string FindKnownSymbol(int sid)
            {
                foreach (var symbolTable in _writer._importContext.Parents)
                {
                    var text = symbolTable.FindKnownSymbol(sid);
                    if (text == null) continue;
                    return text;
                }

                return _writer._locals.FirstOrDefault(kvp => kvp.Value == sid).Key;
            }

            public override IIterator<string> IterateDeclaredSymbolNames() => new PeekIterator<string>(_writer._locals.Keys);
        }
    }
}
