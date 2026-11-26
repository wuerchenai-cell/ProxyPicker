using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;

namespace ProxyPicker.Infrastructure.Storage
{
    /// <summary>脱敏审计日志与崩溃日志落盘服务（支持结构化时间线回读供 v3.0 审计日志页展示）。</summary>
    public sealed class FileAuditSink : IAuditSink
    {
        readonly object _gate = new object();
        readonly string _actionsLogPath;
        readonly string _crashLogPath;
        readonly RedactorService _redactor;
        readonly bool _suppressDiskWrite;

        public event Action<AuditLogEntry> EntryRecorded;

        public FileAuditSink(string ownDataDir, RedactorService redactor, bool suppressDiskWrite = false)
        {
            _actionsLogPath = Path.Combine(ownDataDir, "actions.log");
            _crashLogPath = Path.Combine(ownDataDir, "proxypicker.log");
            _redactor = redactor;
            _suppressDiskWrite = suppressDiskWrite;
        }

        public void Record(string category, string message)
        {
            string cat = string.IsNullOrWhiteSpace(category) ? "INFO" : category.Trim().ToUpperInvariant();
            string cleanMsg = _redactor != null ? _redactor.Apply(message ?? "") : (message ?? "");
            cleanMsg = cleanMsg.Replace("\r", " ").Replace("\n", " ").Trim();
            var entry = new AuditLogEntry
            {
                TimestampLocal = DateTime.Now,
                Category = cat,
                Message = cleanMsg
            };

            if (!_suppressDiskWrite)
            {
                lock (_gate)
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(_actionsLogPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        string line = "[" + entry.TimestampLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] " + cat + " " + cleanMsg + Environment.NewLine;
                        File.AppendAllText(_actionsLogPath, line, new UTF8Encoding(false));
                    }
                    catch { }
                }
            }
            try { EntryRecorded?.Invoke(entry); } catch { }
        }

        public List<AuditLogEntry> ReadRecent(int maxCount = 200)
        {
            var result = new List<AuditLogEntry>();
            lock (_gate)
            {
                if (!File.Exists(_actionsLogPath)) return result;
                try
                {
                    string[] lines = File.ReadAllLines(_actionsLogPath, Encoding.UTF8);
                    int start = Math.Max(0, lines.Length - Math.Max(1, maxCount));
                    for (int i = lines.Length - 1; i >= start; i--)
                    {
                        var m = Regex.Match(lines[i], @"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]\s+([A-Z0-9_]+)\s+(.*)$");
                        if (m.Success)
                        {
                            DateTime dt;
                            DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);
                            result.Add(new AuditLogEntry
                            {
                                TimestampLocal = dt == DateTime.MinValue ? DateTime.Now : dt,
                                Category = m.Groups[2].Value,
                                Message = m.Groups[3].Value
                            });
                        }
                    }
                }
                catch { }
            }
            return result;
        }

        public void ReportCrash(Exception ex, string context)
        {
            if (_suppressDiskWrite || ex == null) return;
            lock (_gate)
            {
                try
                {
                    string dir = Path.GetDirectoryName(_crashLogPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    string raw = "[" + DateTime.Now.ToString("O") + "] ProxyPicker " + ProxyPicker.Domain.ProductInfo.Version +
                                 "\r\n位置=" + (context ?? "") + "\r\n" + ex + "\r\n\r\n";
                    string safe = _redactor != null ? _redactor.Apply(raw) : raw;
                    File.AppendAllText(_crashLogPath, safe, new UTF8Encoding(false));
                }
                catch { }
            }
        }
    }
}
