using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DazzConv.Services
{
    public class RecentOperation
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Type { get; set; } = "";
        public string SourceFormat { get; set; } = "";
        public string TargetFormat { get; set; } = "";
        public int FilesCount { get; set; }
        public Dictionary<string, object> Settings { get; set; } = new();
        public bool Success { get; set; }
    }

    public class RecentOperationsService
    {
        private readonly string _filePath;
        private List<RecentOperation> _operations = new();
        private const int MaxOperations = 50;

        public RecentOperationsService()
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DazzConv");
            Directory.CreateDirectory(appData);
            _filePath = Path.Combine(appData, "recent_operations.json");
            Load();
        }

        public IReadOnlyList<RecentOperation> Operations => _operations.AsReadOnly();

        public void Add(RecentOperation operation)
        {
            _operations.Insert(0, operation);
            if (_operations.Count > MaxOperations)
                _operations = _operations.Take(MaxOperations).ToList();
            Save();
        }

        public List<RecentOperation> GetByType(string type, int count = 5)
        {
            return _operations
                .Where(o => o.Type == type)
                .Take(count)
                .ToList();
        }

        public RecentOperation? GetLast(string type)
        {
            return _operations.FirstOrDefault(o => o.Type == type);
        }

        public void Clear()
        {
            _operations.Clear();
            Save();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    _operations = JsonSerializer.Deserialize<List<RecentOperation>>(json) ?? new();
                }
            }
            catch { }
        }

        private void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_operations, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch { }
        }
    }
}