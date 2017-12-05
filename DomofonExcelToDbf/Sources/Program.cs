﻿using DomofonExcelToDbf.Sources.Xml;
using Microsoft.Office.Interop.Excel;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;
using DomofonExcelToDbf.Properties;
using DomofonExcelToDbf.Sources.Core;
using DomofonExcelToDbf.Sources.View;
using Application = System.Windows.Forms.Application;
using Point = System.Drawing.Point;

namespace DomofonExcelToDbf.Sources
{
    [SuppressMessage("ReSharper", "PossibleNullReferenceException")]
    public class Program
    {

        [STAThread]
        private static void Main()
        {
            bool exists = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly().Location)).Length > 1;
            if (exists)
            {
                MessageBox.Show("Программа уже запущена!", "Предупреждение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Распаковка DLL, которая не находится при упаковке через LibZ
            File.WriteAllBytes("Microsoft.WindowsAPICodePack.dll", Resources.Microsoft_WindowsAPICodePack);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Program program = new Program();
            MainWindow window = new MainWindow(program);
            window.FormClosing += program.onFormMainClosing;
            Application.Run(window);
        }

        string confName;
        public XDocument xdoc;
        public Xml_Config config;
        public bool showStacktrace = false;
        Thread process;

        public Dictionary<string, string> formToFile = new Dictionary<string, string>();
        public List<string> outlog = new List<string>();
        public List<string> errlog = new List<string>();
        public HashSet<string> filesExcel = new HashSet<string>();
        public HashSet<string> filesDBF = new HashSet<string>();

        public void init()
        {
            confName = Path.ChangeExtension(AppDomain.CurrentDomain.FriendlyName, ".xml");

            if (!File.Exists(confName))
            {
                Console.WriteLine(@"Не найден конфигурационный файл!");
                Console.WriteLine(@"Распаковываем его из внутренних ресурсов...");
                WriteResourceToFile("xConfig", confName);
            }

            config = Xml_Config.Load(confName);
            xdoc = XDocument.Load(confName);

            if (!Directory.Exists("logs")) Directory.CreateDirectory("logs");
            Logger.instance = new Logger(config.log ? "logs\\" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log" : null, Logger.LogLevel.TRACER);

            updateDirectory();

            Logger.info("Версия программы: " + Resources.version);
        }

        public void updateDirectory()
        {
            Logger.debug("Директория чтения: " + config.inputDirectory);
            Logger.debug("Директория записи: " + config.outputDirectory);

            if (!Directory.Exists(config.inputDirectory)) config.inputDirectory = Directory.GetCurrentDirectory();
            if (!Directory.Exists(config.outputDirectory)) config.outputDirectory = Directory.GetCurrentDirectory();

            filesDBF.Clear();
            filesExcel.Clear();

            string[] fbyext = Directory.GetFiles(config.outputDirectory, "*.dbf", SearchOption.TopDirectoryOnly);
            filesDBF.UnionWith(fbyext);

            foreach (string extension in config.extensions)
            {
                fbyext = Directory.GetFiles(config.inputDirectory, extension, SearchOption.TopDirectoryOnly);
                fbyext = fbyext.Where(path => path != null
                        && !Path.GetFileName(path).Equals(confName) // А также наш конфигурационный файл %EXE_NAME%.xml
                        && !Path.GetFileName(path).StartsWith("~$")).ToArray(); // Игнорируем временные файлы Excel вида ~$Document.xls[x]
                filesExcel.UnionWith(fbyext);
            }
        }

        private void onFormMainClosing(object sender, FormClosingEventArgs e)
        {
            onCloseCheckProcess(e);

            xdoc.Root.Element("inputDirectory").Value = config.inputDirectory;
            xdoc.Root.Element("outputDirectory").Value = config.outputDirectory;
            xdoc.Save(confName);
        }

        private void onCloseCheckProcess(FormClosingEventArgs e)
        {
            if (process == null) return;
            DialogResult abort = DialogResult.None;

            if (process.IsAlive)
            {
                abort = MessageBox.Show("Вы действительно хотите выйти?\nПроцесс конвертирования будет прерван.", "Предупреждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            }

            if (abort == DialogResult.No)
            {
                e.Cancel = true;
                return;
            }

            process.Abort();
        }

        public void action(MainWindow wmain, HashSet<string> files)
        {
            if (process != null && process.IsAlive)
            {
                MessageBox.Show("Процесс конвертирования уже запущен!\nДождись его завершения, если вы хотите начать новый.", "Предупреждение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            StatusWindow wstatus = new StatusWindow();
            wstatus.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason != CloseReason.UserClosing) return;
                if (wstatus.codeClose) return;
                e.Cancel = DialogResult.No == MessageBox.Show("Вы действительно хотите прервать обработку файлов?", "Внимание", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (!e.Cancel)
                {
                    process.Abort();
                    wstatus.Hide();
                    MessageBox.Show(wmain, "Документы не были обработаны: процесс был прерван пользователем!", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            wstatus.Location = new Point(
                wmain.Location.X + ((wmain.Width - wstatus.Width) / 2),
                wmain.Location.Y + ((wmain.Height - wstatus.Height) / 2)
            );
            wstatus.Show(wmain);
            // Альтернативный вариант:
            //wstatus.StartPosition = FormStartPosition.CenterParent;
            //wstatus.ShowDialog(wmain);

            object data = new object[] { wstatus, wmain, files };

            outlog.Clear();
            errlog.Clear();
            formToFile.Clear();

            process = new Thread(delegate_action);
            process.Start(data);
        }

        protected void delegate_action(object obj)
        {
            object [] data = (object[])obj;

            StatusWindow window = (StatusWindow)data[0];
            MainWindow wmain = (MainWindow)data[1];
            HashSet<string> files = (HashSet<string>)data[2];
            window.setState(true, "Подготовка файлов", 0, files.Count);
            int idoc = 1;

            Excel excel = new Excel(config.save_memory);
            DBF dbf = null;

            var totalwatch = new Stopwatch();
            totalwatch.Start();
            foreach (string fname in files)
            {

                // COM Excel требуется полный путь до файла
                string finput = Path.GetFullPath(fname);

                bool deleteDbf = false;

                try
                {
                    Logger.debug("");
                    Logger.info("");
                    Logger.debug("==============================================================");
                    Logger.info($"======= Загружаем Excel документ: {Path.GetFileName(finput)} ======");
                    Logger.debug("==============================================================");
                    window.updateState(true, $"Документ: {Path.GetFileName(finput)}", idoc);
                    idoc++;

                    excel.OpenWorksheet(finput);

                    var form = findCorrectForm(excel.worksheet, config);

                    if (config.only_rules)
                    {
                        var formname = (form != null) ? form.Name : "null";
                        formToFile.Add(Path.GetFileName(finput), formname);
                        continue;
                    }

                    if (form == null)
                    {
                        Logger.warn("Не найдено подходящих форм для обработки документа work.xml!");
                        throw new NoNullAllowedException("Не найдено подходящих форм для обработки документа work.xml!");
                    }

                    string fileName = getOutputFilename(excel.worksheet, finput, config.outfile.simple, config.outfile.script);
                    string pathTemp = Path.GetTempFileName();
                    string pathOutput = Path.Combine(config.outputDirectory, fileName);

                    var total = excel.worksheet.UsedRange.Rows.Count - form.Fields.StartY;
                    window.setState(false, $"Обработано записей: {0}/{total}", 0, total);

                    dbf = new DBF(pathTemp,form.DBF);
                    dbf.writeHeader();

                    var stopwatch = new Stopwatch();
                    stopwatch.Start();
                    Work work = new Work(form, config.buffer_size);
                    work.IterateRecords(excel.worksheet, dbf.appendRecord,
                        id => window.updateState(false, $"Обработано записей: {id}/{total}", id)
                    );
                    stopwatch.Stop();

                    dbf.close();

                    Logger.info("Времени потрачено на обработку данных: " + stopwatch.Elapsed);
                    Logger.info("Обработано записей: " + dbf.records);
                    outlog.Add($"{Path.GetFileName(finput)} в {dbf.records} строк за {stopwatch.Elapsed:hh\\:mm\\:ss\\.ff}");

                    int startY = form.Fields.StartY;
                    Logger.debug($"Начиная с {startY} по {startY + dbf.records}");

                    // Перемещение файла
                    if (File.Exists(pathOutput)) File.Delete(pathOutput);
                    File.Move(pathTemp, pathOutput);
                    Logger.debug($"Перемещение файла с {pathTemp} в {pathOutput}");

                    Logger.info($"=============== Документ {Path.GetFileName(finput)} успешно обработан! ===============");
                }
                catch (Exception ex) when (!Debugger.IsAttached)
                {
                    if (ex is ThreadAbortException)
                    {
                        excel.close();
                        goto skip_error_msgbox;
                    }

                    errlog.Add($"Документ \"{Path.GetFileName(finput)}\" был пропущен!");

                    string stacktrace = (showStacktrace) ? ex.StackTrace : "";

                    var message = $"Ошибка! Документ \"{Path.GetFileName(finput)}\" будет пропущен!\n\n{ex.Message}\n\n{stacktrace}";
                    Logger.error(message + "\n" + ex.StackTrace);
                    MessageBox.Show(message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);

                    skip_error_msgbox:
                    Console.Error.WriteLine(ex);
                    deleteDbf = true;
                }
                finally
                {
                    Logger.debug("Закрытие COM Excel и DBF");
                    dbf?.close();
                    if (deleteDbf) dbf?.delete();
                }

            }
            totalwatch.Stop();

            // Не забываем завершить Excel
            excel.close();

            string crules = "";

            if (config.only_rules)
            {
                for (int i = 0; i < 3; i++) Logger.debug("");
                foreach (var tup in formToFile)
                {
                    string line = $"Для \"{tup.Key}\" выбрана форма \"{tup.Value}\"";
                    Logger.info(line);
                    crules += line + "\n";
                }
                MessageBox.Show(crules, "Отчёт о формах", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            crules = "Время обработки документов:\n";

            var coutlog = String.Join("\n", outlog) + "\n";
            crules += coutlog;
            Logger.info(coutlog);

            Logger.info("Времени затрачено суммарно: " + totalwatch.Elapsed);
            crules += String.Format("\nВремени затрачено суммарно: " + totalwatch.Elapsed.ToString("hh\\:mm\\:ss\\.ff"));

            var icon = MessageBoxIcon.Information;

            if (errlog.Count > 0) {
                icon = MessageBoxIcon.Warning;
                crules += "\n\n";

                var xmlWarning = xdoc.Root.Element("warning");
                string warnFormat = xmlWarning?.Value ?? "{0}";
                warnFormat = warnFormat.Replace("\\n", "\n");
                crules += String.Format(warnFormat,string.Join("\n", errlog));
            }

            updateDirectory();
            wmain.BeginInvoke((MethodInvoker)wmain.fillElementsData);

            window.mayClose();
            MessageBox.Show(crules, "Отчёт о времени обработки", MessageBoxButtons.OK, icon);
        }

        public string getOutputFilename(Worksheet worksheet, String inputFile, bool simple, string script = null)
        {
            if (simple) return Path.GetFileName(Path.ChangeExtension(inputFile, ".dbf"));

            JS.DelegateReadExcel readCell = (x, y) =>
            {
                try
                {
                    return worksheet.Cells[y, x].Value;
                }
                catch (Exception ex)
                {
                    Logger.warn($"Ошибка при чтении ячейки x={x},y={y}: {ex.Message}");
                    return null;
                }
            };

            JS js = new JS(readCell, Logger.info);
            js.SetPath(inputFile);

            string outputFilename = js.Execute(script);
            if (!outputFilename.EndsWith(".dbf")) outputFilename += ".dbf";
            return outputFilename;
        }

        // <summary>
        // Ищет подходящую XML форму для документа или null если ни одна не подходит
        // </summary>
        public Xml_Form findCorrectForm(Worksheet worksheet, Xml_Config pConfig)
        {
            RegExCache regExCache = new RegExCache();

            foreach (Xml_Form form in pConfig.Forms)
            {
                bool correct = true;
                Logger.info("");
                Logger.info($"Проверяем форму \"{form.Name}\"");
                Logger.debug("==========================================");

                int index = 1;
                foreach (Xml_Equal rule in form.Rules)
                {
                    bool useRegex = rule.regex_pattern != null;
                    bool validateRegex = rule.validate == "regex";

                    string cell;

                    try
                    {
                        cell = worksheet.Cells[rule.Y, rule.X].Value.ToString();
                    }
                    catch (Exception ex)
                    {
                        Logger.debug($"Произошла ошибка при чтении ячейки Y={rule.Y},X={rule.X}!");
                        Logger.debug($"Ожидалось: {rule.Text}");
                        Logger.debug("Ошибка: " + ex.Message);
                        Logger.info($"Форма не подходит по условию №{index}");
                        correct = false;
                        break;
                    }

                    string origcell = cell;
                    if (useRegex && !validateRegex)
                    {
                        cell = regExCache.MatchGroup(cell, rule.regex_pattern, rule.regex_group);
                    }

                    bool failed = false;
                    if (rule.Text != cell && !validateRegex) failed = true;
                    if (validateRegex && !regExCache.IsMatch(cell, rule.Text)) failed = true;

                    if (failed)
                    {
                        if (validateRegex || useRegex) Logger.debug("Провалена проверка по регулярному выражению!");
                        Logger.debug($"Проверка провалена (Y={rule.Y},X={rule.X})");
                        Logger.debug($"Ожидалось: {rule.Text}");
                        Logger.debug($"Найдено: {cell}");
                        if (useRegex)
                        {
                            Logger.debug($"Оригинальная ячейка: {origcell}");
                            Logger.debug($"Регулярное выражение: {rule.regex_pattern}");
                            Logger.debug($"Группа для поиска: {rule.regex_group}");
                        }
                        Logger.info($"Форма не подходит по условию №{index}");
                        correct = false;
                        break;
                    }
                    Logger.debug($"Y={rule.Y},X={rule.X}: {rule.Text}{(validateRegex ? " is match" : "==")}{cell}");
                    index++;
                }
                if (correct)
                {
                    Logger.info($"Форма '{form.Name}' подходит для документа!");
                    return form;
                }
            }
            return null;
        }

        // <summary>
        // Метод считывает внутренний ресурс и записывает его в файл, возвращая статус существования ресурса
        // </summary>
        // <param name="resourceName">Имя внутренного ресурса</param>
        // <param name="fileName">Имя внутренного ресурса</param>
        // <returns>false если внутренний ресурс не был найден</returns>
        public static bool WriteResourceToFile(string resourceName, string fileName)
        {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (resource == null) return false;
                using (var file = new FileStream(fileName, FileMode.Create, FileAccess.Write))
                {
                    resource.CopyTo(file);
                }
            }
            return true;
        }
    }
}