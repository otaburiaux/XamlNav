using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;

namespace XamlNav
{
    /// <summary>
    /// Data source that feeds reference entries into the VS Find All References window
    /// via the ITableManager / ITableDataSink infrastructure.
    /// </summary>
    internal sealed class XamlReferenceTableDataSource : ITableDataSource
    {
        private readonly List<XamlReferenceEntry> _entries = new List<XamlReferenceEntry>();
        private readonly List<ITableDataSink> _sinks = new List<ITableDataSink>();
        private readonly object _lock = new object();
        private int _snapshotVersion;

        public string SourceTypeIdentifier => StandardTableDataSources.AnyDataSource;
        public string Identifier => "XamlNav.FindAllReferences";
        public string DisplayName => "XAML References";

        /// <summary>
        /// Called by the table manager when the FAR window subscribes to this source.
        /// </summary>
        public IDisposable Subscribe(ITableDataSink sink)
        {
            lock (_lock)
            {
                _sinks.Add(sink);

                // If we already have entries, push them immediately
                if (_entries.Count > 0)
                {
                    sink.AddSnapshot(new XamlReferenceSnapshot(_entries.ToList(), _snapshotVersion));
                }
            }

            return new SinkDisposer(this, sink);
        }

        /// <summary>
        /// Adds a batch of entries and notifies all subscribed sinks.
        /// </summary>
        public void AddEntries(IReadOnlyList<XamlReferenceEntry> entries)
        {
            if (entries == null || entries.Count == 0) return;

            lock (_lock)
            {
                _entries.AddRange(entries);
                _snapshotVersion++;
                var snapshot = new XamlReferenceSnapshot(_entries.ToList(), _snapshotVersion);

                foreach (var sink in _sinks)
                {
                    sink.AddSnapshot(snapshot);
                }
            }
        }

        /// <summary>
        /// Marks the search as complete on all sinks.
        /// </summary>
        public void Complete()
        {
            lock (_lock)
            {
                foreach (var sink in _sinks)
                {
                    sink.IsStable = true;
                }
            }
        }

        private void RemoveSink(ITableDataSink sink)
        {
            lock (_lock)
            {
                _sinks.Remove(sink);
            }
        }

        /// <summary>
        /// Disposable token returned from Subscribe; removes the sink on dispose.
        /// </summary>
        private sealed class SinkDisposer : IDisposable
        {
            private readonly XamlReferenceTableDataSource _source;
            private readonly ITableDataSink _sink;

            public SinkDisposer(XamlReferenceTableDataSource source, ITableDataSink sink)
            {
                _source = source;
                _sink = sink;
            }

            public void Dispose()
            {
                _source.RemoveSink(_sink);
            }
        }

        /// <summary>
        /// Snapshot of reference entries implementing ITableEntriesSnapshot.
        /// </summary>
        private sealed class XamlReferenceSnapshot : ITableEntriesSnapshot
        {
            private readonly IReadOnlyList<XamlReferenceEntry> _entries;

            public XamlReferenceSnapshot(IReadOnlyList<XamlReferenceEntry> entries, int version)
            {
                _entries = entries;
                VersionNumber = version;
            }

            public int Count => _entries.Count;
            public int VersionNumber { get; }

            public void Dispose() { }

            public int IndexOf(int currentIndex, ITableEntriesSnapshot newSnapshot)
            {
                return currentIndex;
            }

            public void StartCaching() { }
            public void StopCaching() { }

            public bool TryGetValue(int index, string keyName, out object content)
            {
                if (index < 0 || index >= _entries.Count)
                {
                    content = null;
                    return false;
                }

                return _entries[index].TryGetValue(keyName, out content);
            }

            public bool CanCreateDetailsContent(int index) => false;

            public bool TryCreateColumnContent(int index, string columnName, bool singleColumnView, out object content)
            {
                if (index >= 0 && index < _entries.Count &&
                    (columnName == StandardTableColumnDefinitions2.LineText ||
                     columnName == StandardTableColumnDefinitions.Text))
                {
                    var inlines = _entries[index].CreateLineTextInlines();
                    if (inlines != null && inlines.Count > 0)
                    {
                        var textBlock = new TextBlock();
                        textBlock.Inlines.AddRange(inlines);
                        content = textBlock;
                        return true;
                    }
                }

                content = null;
                return false;
            }

            public bool TryCreateDetailsContent(int index, out object expandedContent)
            {
                expandedContent = null;
                return false;
            }

            public bool TryCreateDetailsStringContent(int index, out string content)
            {
                content = null;
                return false;
            }

            public bool TryCreateToolTip(int index, string columnName, out object toolTip)
            {
                toolTip = null;
                return false;
            }

            public bool TryCreateStringContent(int index, string columnName, bool truncatedText, bool singleColumnView, out string content)
            {
                content = null;
                return false;
            }

            public bool TryCreateImageContent(int index, string columnName, bool singleColumnView, out object content)
            {
                content = null;
                return false;
            }
        }
    }
}
