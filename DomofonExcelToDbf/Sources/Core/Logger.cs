﻿using System;
using System.IO;

namespace DomofonExcelToDbf.Sources.Core
{
    public class Logger
    {
        readonly bool console;
        readonly StreamWriter writer;
        protected LogLevel level;

        public static Logger instance;

        public Logger(string file = null, LogLevel level = LogLevel.INFO)
        {
            this.level = level;

            console = (file == null);
            if (file != null)
            {
                writer = new StreamWriter(file, false) {AutoFlush = true};
            }
        }

        protected void _log(object data, LogLevel curLevel)
        {
            if (curLevel > level) return;

            string msg = $"[{curLevel}][{DateTime.Now:HH:mm:ss}] {data}";

            Console.WriteLine(msg);
            if (!console)
            {
                writer.WriteLine(msg);
                writer.Flush();
            }
        }

        public enum LogLevel : byte
        {
            CRITICAL,
            ERROR,
            WARN,
            INFO,
            DEBUG,
            TRACER
        }

        public static void SetLevel(LogLevel newLevel)
        {
            instance.level = newLevel;
        }

        public static void tracer(object data)
        {
            instance._log(data, LogLevel.TRACER);
        }

        public static void error(object data)
        {
            instance._log(data, LogLevel.ERROR);
        }

        public static void warn(object data)
        {
            instance._log(data, LogLevel.WARN);
        }

        public static void info(object data)
        {
            instance._log(data,LogLevel.INFO);
        }

        public static void debug(object data)
        {
            instance._log(data,LogLevel.DEBUG);
        }

        public static void log(object data, LogLevel level = LogLevel.INFO)
        {
            instance._log(data,level);
        }
    }

}
