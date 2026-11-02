using System.Data.Common;

namespace Encoding.Api.Infrastructure.Data;

/// <summary>Preserves transaction and admission lifetimes until a result reader closes.</summary>
internal sealed class TransactionReader(DbDataReader inner, DbTransaction? transaction, DbConnection connection,
    bool closeConnection, IDisposable? writerLease = null, Action? onClose = null) : DbDataReader
{
    private bool disposed;
    public override object this[int ordinal] => inner[ordinal];
    public override object this[string name] => inner[name];
    public override int Depth => inner.Depth;
    public override int FieldCount => inner.FieldCount;
    public override int VisibleFieldCount => inner.VisibleFieldCount;
    public override System.Data.DataTable? GetSchemaTable() => inner.GetSchemaTable();
    public override bool HasRows => inner.HasRows;
    public override bool IsClosed => disposed || inner.IsClosed;
    public override int RecordsAffected => inner.RecordsAffected;
    public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
    public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, offset, buffer, bufferOffset, length);
    public override char GetChar(int ordinal) => inner.GetChar(ordinal);
    public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, offset, buffer, bufferOffset, length);
    public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
    public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
    public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
    public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
    public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
    public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
    public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
    public override string GetName(int ordinal) => inner.GetName(ordinal);
    public override int GetOrdinal(string name) => inner.GetOrdinal(name);
    public override string GetString(int ordinal) => inner.GetString(ordinal);
    public override object GetValue(int ordinal) => inner.GetValue(ordinal);
    public override int GetValues(object[] values) => inner.GetValues(values);
    public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
    public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);
    public override System.Collections.IEnumerator GetEnumerator() => ((System.Collections.IEnumerable)inner).GetEnumerator();
    public override bool NextResult() => inner.NextResult();
    public override bool Read() => inner.Read();
    public override Task<bool> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct);
    public override Task<bool> NextResultAsync(CancellationToken ct) => inner.NextResultAsync(ct);
    public override void Close() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    protected override void Dispose(bool disposing) { if (disposing) Close(); }
    public override async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            await inner.DisposeAsync();
            if (transaction is not null) await transaction.CommitAsync();
        }
        finally
        {
            try { if (transaction is not null) await transaction.DisposeAsync(); }
            finally
            {
                onClose?.Invoke();
                writerLease?.Dispose();
                if (closeConnection) await connection.CloseAsync();
            }
        }
    }
}
