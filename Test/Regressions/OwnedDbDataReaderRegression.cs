using Customize_ORM.Extensions.Sql;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Test
{
    internal static class OwnedDbDataReaderRegression
    {
        public static void Run()
        {
            var failures = new List<string>();

            RunCase(
                failures,
                "OwnedDbDataReader keeps the command alive until the reader is disposed",
                () =>
                {
                    var innerReader = CreateReader();
                    var command = new TrackingDbCommand();
                    var reader = new OwnedDbDataReader(innerReader, command);

                    if (!reader.Read())
                    {
                        throw new InvalidOperationException("Expected the wrapped reader to expose the inner row.");
                    }

                    if (command.DisposeCount != 0)
                    {
                        throw new InvalidOperationException($"Command was disposed too early. DisposeCount={command.DisposeCount}.");
                    }

                    reader.Dispose();

                    if (command.DisposeCount != 1)
                    {
                        throw new InvalidOperationException($"Expected DisposeCount=1 but found {command.DisposeCount}.");
                    }
                });

            RunCase(
                failures,
                "OwnedDbDataReader only disposes the command once across Close and Dispose",
                () =>
                {
                    var command = new TrackingDbCommand();
                    var reader = new OwnedDbDataReader(CreateReader(), command);

                    reader.Close();
                    reader.Dispose();

                    if (command.DisposeCount != 1)
                    {
                        throw new InvalidOperationException($"Expected DisposeCount=1 but found {command.DisposeCount}.");
                    }
                });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "OwnedDbDataReader regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failures));
            }

            Console.WriteLine("OwnedDbDataReader regression harness passed.");
        }

        private static DbDataReader CreateReader()
        {
            var table = new DataTable();
            table.Columns.Add("id", typeof(int));
            table.Rows.Add(1);
            return table.CreateDataReader();
        }

        private static void RunCase(List<string> failures, string name, Action assertion)
        {
            try
            {
                assertion();
                Console.WriteLine($"[PASS] {name}");
            }
            catch (Exception ex)
            {
                failures.Add($"[FAIL] {name}{Environment.NewLine}{ex.GetType().FullName}: {ex.Message}");
                Console.WriteLine($"[FAIL] {name}");
            }
        }

        private sealed class TrackingDbCommand : DbCommand
        {
            private readonly TrackingParameterCollection _parameters = new();

            public int DisposeCount { get; private set; }

            [AllowNull]
            public override string CommandText { get; set; } = string.Empty;

            public override int CommandTimeout { get; set; }

            public override CommandType CommandType { get; set; }

            public override bool DesignTimeVisible { get; set; }

            public override UpdateRowSource UpdatedRowSource { get; set; }

            protected override DbConnection? DbConnection { get; set; }

            protected override DbParameterCollection DbParameterCollection => _parameters;

            protected override DbTransaction? DbTransaction { get; set; }

            public override void Cancel()
            {
            }

            public override int ExecuteNonQuery() => throw new NotSupportedException();

            public override object? ExecuteScalar() => throw new NotSupportedException();

            public override void Prepare()
            {
            }

            protected override DbParameter CreateDbParameter() => new TrackingDbParameter();

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    DisposeCount++;
                }

                base.Dispose(disposing);
            }
        }

        private sealed class TrackingDbParameter : DbParameter
        {
            public override DbType DbType { get; set; }

            public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

            public override bool IsNullable { get; set; }

            [AllowNull]
            public override string ParameterName { get; set; } = string.Empty;

            [AllowNull]
            public override string SourceColumn { get; set; } = string.Empty;

            public override object? Value { get; set; }

            public override bool SourceColumnNullMapping { get; set; }

            public override int Size { get; set; }

            public override void ResetDbType()
            {
            }
        }

        private sealed class TrackingParameterCollection : DbParameterCollection
        {
            private readonly List<DbParameter> _parameters = [];

            public override int Count => _parameters.Count;

            public override object SyncRoot => ((ICollection)_parameters).SyncRoot;

            public override int Add(object value)
            {
                _parameters.Add((DbParameter)value);
                return _parameters.Count - 1;
            }

            public override void AddRange(Array values)
            {
                foreach (var value in values)
                {
                    Add(value!);
                }
            }

            public override void Clear() => _parameters.Clear();

            public override bool Contains(object value) => _parameters.Contains((DbParameter)value);

            public override bool Contains(string value)
                => _parameters.Any(parameter => string.Equals(parameter.ParameterName, value, StringComparison.Ordinal));

            public override void CopyTo(Array array, int index) => ((ICollection)_parameters).CopyTo(array, index);

            public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

            protected override DbParameter GetParameter(int index) => _parameters[index];

            protected override DbParameter GetParameter(string parameterName)
                => _parameters.First(parameter => string.Equals(parameter.ParameterName, parameterName, StringComparison.Ordinal));

            public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);

            public override int IndexOf(string parameterName)
                => _parameters.FindIndex(parameter => string.Equals(parameter.ParameterName, parameterName, StringComparison.Ordinal));

            public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);

            public override void Remove(object value) => _parameters.Remove((DbParameter)value);

            public override void RemoveAt(int index) => _parameters.RemoveAt(index);

            public override void RemoveAt(string parameterName)
            {
                var index = IndexOf(parameterName);
                if (index >= 0)
                {
                    _parameters.RemoveAt(index);
                }
            }

            protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;

            protected override void SetParameter(string parameterName, DbParameter value)
            {
                var index = IndexOf(parameterName);
                if (index >= 0)
                {
                    _parameters[index] = value;
                    return;
                }

                _parameters.Add(value);
            }
        }
    }
}
