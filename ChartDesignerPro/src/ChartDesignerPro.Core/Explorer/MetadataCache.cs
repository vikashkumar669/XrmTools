using System;
using System.Collections.Generic;
using System.Threading;
using ChartDesignerPro.Abstractions;
using ChartDesignerPro.Models;

namespace ChartDesignerPro.Explorer
{
    /// <summary>
    /// Caches metadata from an <see cref="IMetadataProvider"/> for a limited time, so metadata is never stale indefinitely.
    /// One cache belongs to one connection; the plugin creates a new cache when the connection changes.
    /// Thread-safe: metadata is read on background workers.
    /// </summary>
    public sealed class MetadataCache : IMetadataProvider
    {
        private readonly IMetadataProvider _inner;
        private readonly IClock _clock;
        private readonly object _gate = new object();
        private readonly Dictionary<string, TableMetadata> _tables = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<TableSummary> _tableList;
        private DateTime _tableListRetrievedUtc;

        public MetadataCache(IMetadataProvider inner, TimeSpan timeToLive, IClock clock = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (timeToLive <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeToLive));
            TimeToLive = timeToLive;
            _clock = clock ?? SystemClock.Instance;
        }

        public TimeSpan TimeToLive { get; }

        public IReadOnlyList<TableSummary> ListTables(CancellationToken ct)
        {
            lock (_gate)
            {
                if (_tableList != null && !IsExpired(_tableListRetrievedUtc)) return _tableList;
            }
            var fresh = _inner.ListTables(ct);
            lock (_gate)
            {
                _tableList = fresh;
                _tableListRetrievedUtc = _clock.UtcNow;
            }
            return fresh;
        }

        public TableMetadata GetTable(string logicalName)
        {
            if (string.IsNullOrEmpty(logicalName)) throw new ArgumentNullException(nameof(logicalName));
            lock (_gate)
            {
                if (_tables.TryGetValue(logicalName, out var cached) && !IsExpired(cached.RetrievedUtc)) return cached;
            }
            var fresh = _inner.GetTable(logicalName);
            if (fresh == null) return null;
            fresh.RetrievedUtc = _clock.UtcNow;
            lock (_gate)
            {
                _tables[logicalName] = fresh;
            }
            return fresh;
        }

        /// <summary>Drops everything, so the next call goes to Dataverse ("Refresh metadata").</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _tables.Clear();
                _tableList = null;
            }
        }

        private bool IsExpired(DateTime retrievedUtc) => _clock.UtcNow - retrievedUtc >= TimeToLive;
    }
}
