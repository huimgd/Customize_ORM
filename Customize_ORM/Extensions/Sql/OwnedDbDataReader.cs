using System.Collections;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Threading;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// Keeps the owning command alive for the lifetime of the reader and disposes it when the reader is closed.
    /// </summary>
    internal sealed class OwnedDbDataReader : DbDataReader
    {
        private readonly DbDataReader _innerReader;
        private readonly DbCommand _owningCommand;
        private int _commandDisposed;

        public OwnedDbDataReader(DbDataReader innerReader, DbCommand owningCommand)
        {
            _innerReader = innerReader ?? throw new ArgumentNullException(nameof(innerReader));
            _owningCommand = owningCommand ?? throw new ArgumentNullException(nameof(owningCommand));
        }

        public override object this[int ordinal] => _innerReader[ordinal];

        public override object this[string name] => _innerReader[name];

        public override int Depth => _innerReader.Depth;

        public override int FieldCount => _innerReader.FieldCount;

        public override bool HasRows => _innerReader.HasRows;

        public override bool IsClosed => _innerReader.IsClosed;

        public override int RecordsAffected => _innerReader.RecordsAffected;

        public override void Close()
        {
            try
            {
                _innerReader.Close();
            }
            finally
            {
                DisposeCommand();
            }
        }

        public override DataTable? GetSchemaTable() => _innerReader.GetSchemaTable();

        public override bool NextResult() => _innerReader.NextResult();

        public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
            => _innerReader.NextResultAsync(cancellationToken);

        public override bool Read() => _innerReader.Read();

        public override Task<bool> ReadAsync(CancellationToken cancellationToken)
            => _innerReader.ReadAsync(cancellationToken);

        public override int VisibleFieldCount => _innerReader.VisibleFieldCount;

        public override bool GetBoolean(int ordinal) => _innerReader.GetBoolean(ordinal);

        public override byte GetByte(int ordinal) => _innerReader.GetByte(ordinal);

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
            => _innerReader.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

        public override char GetChar(int ordinal) => _innerReader.GetChar(ordinal);

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
            => _innerReader.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

        public override string GetDataTypeName(int ordinal) => _innerReader.GetDataTypeName(ordinal);

        public override DateTime GetDateTime(int ordinal) => _innerReader.GetDateTime(ordinal);

        public override decimal GetDecimal(int ordinal) => _innerReader.GetDecimal(ordinal);

        public override double GetDouble(int ordinal) => _innerReader.GetDouble(ordinal);

        public override Type GetFieldType(int ordinal) => _innerReader.GetFieldType(ordinal);

        public override float GetFloat(int ordinal) => _innerReader.GetFloat(ordinal);

        public override Guid GetGuid(int ordinal) => _innerReader.GetGuid(ordinal);

        public override short GetInt16(int ordinal) => _innerReader.GetInt16(ordinal);

        public override int GetInt32(int ordinal) => _innerReader.GetInt32(ordinal);

        public override long GetInt64(int ordinal) => _innerReader.GetInt64(ordinal);

        public override string GetName(int ordinal) => _innerReader.GetName(ordinal);

        public override int GetOrdinal(string name) => _innerReader.GetOrdinal(name);

        public override string GetString(int ordinal) => _innerReader.GetString(ordinal);

        public override object GetValue(int ordinal) => _innerReader.GetValue(ordinal);

        public override int GetValues(object[] values) => _innerReader.GetValues(values);

        public override bool IsDBNull(int ordinal) => _innerReader.IsDBNull(ordinal);

        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
            => _innerReader.IsDBNullAsync(ordinal, cancellationToken);

        public override IEnumerator GetEnumerator() => ((IEnumerable)_innerReader).GetEnumerator();

        public override T GetFieldValue<T>(int ordinal) => _innerReader.GetFieldValue<T>(ordinal);

        public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
            => _innerReader.GetFieldValueAsync<T>(ordinal, cancellationToken);

        public override Stream GetStream(int ordinal) => _innerReader.GetStream(ordinal);

        public override TextReader GetTextReader(int ordinal) => _innerReader.GetTextReader(ordinal);

        public override object GetProviderSpecificValue(int ordinal) => _innerReader.GetProviderSpecificValue(ordinal);

        public override int GetProviderSpecificValues(object[] values) => _innerReader.GetProviderSpecificValues(values);

        public override Type GetProviderSpecificFieldType(int ordinal) => _innerReader.GetProviderSpecificFieldType(ordinal);

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    _innerReader.Dispose();
                }
            }
            finally
            {
                DisposeCommand();
                base.Dispose(disposing);
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await _innerReader.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                DisposeCommand();
            }
        }

        private void DisposeCommand()
        {
            if (Interlocked.Exchange(ref _commandDisposed, 1) == 0)
            {
                _owningCommand.Dispose();
            }
        }
    }
}
