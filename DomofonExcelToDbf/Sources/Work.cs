﻿using Microsoft.Office.Interop.Excel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DomofonExcelToDbf.Sources.Xml;

namespace DomofonExcelToDbf.Sources
{
    public class Work
    {
        public Dictionary<string, TVariable> staticVars = new Dictionary<string, TVariable>();
        public Dictionary<string, TVariable> dynamicVars = new Dictionary<string, TVariable>();
        public HashSet<TCondition> conditions = new HashSet<TCondition>();

        protected int startY;
        protected int endX;
        protected int buffer;
        protected int total = 0;
        protected List<Xml_Validator> validators;
        protected TVariable exception_var;

        public Dictionary<string, TVariable> stepScope = new Dictionary<string, TVariable>();

        public Work(XDocument xdocument, Xml_Form form, int buffer)
        {
            InitVariables(form);
            startY = form.Fields.StartY;
            endX = form.Fields.EndX;
            validators = form.Validate;
            this.buffer = buffer;
        }

        public void IterateRecords(Worksheet worksheet, Action<Dictionary<string, TVariable>> callback, Action<int> guiCallback = null)
        {
            if (buffer <= 0) throw new ArgumentException("Буфер обработки должен быть больше ноля!");
            total = 0;
            try
            {
                __IterateRecords(worksheet, callback, guiCallback);
            }
            catch (Exception ex)
            {
                string message = string.Format("Ошибка на строке {0}, ячейке {1} в переменной {2}:\n{3}", startY + total, exception_var.x, exception_var.name, ex.Message);
                throw new MyException(message, ex);
            }
            FinalChecks();
        }

        protected void __IterateRecords(Worksheet worksheet, Action<Dictionary<string, TVariable>> callback, Action<int> guiCallback = null)
        {
            int begin = startY;
            int end = startY + buffer;

            var maxY = worksheet.UsedRange.Rows.Count;

            Stopwatch watch;
            bool EOF = false;

            watch = Stopwatch.StartNew();
            stepScope.Clear();
            foreach (var var in staticVars.Values)
            {
                exception_var = var;
                var.Set(worksheet.Cells[var.y, var.x].Value);
                stepScope.Add(var.name, var);
            }
            watch.Stop();
            Logger.instance.log("Заполнение массива локальных переменных: " + watch.ElapsedMilliseconds);

            Stopwatch watchTotal = Stopwatch.StartNew();
            while (!EOF)
            {
                var range_start = worksheet.Cells[begin, 1];
                var range_end = worksheet.Cells[end, endX];
                var range = worksheet.Range[range_start, range_end];
                object[,] tmp = range.Value;

                watch = Stopwatch.StartNew();
                for (int i = 1; i <= buffer; i++)
                {
                    bool skipRecord = false;
                    bool stopLoop = false;

                    foreach (TCondition cond in conditions)
                    {
                        if (cond.mustBe.Equals(tmp[i, cond.x]) || (cond.mustBe == "" && tmp[i, cond.x] == null))
                        {
                            foreach (TAction item in cond.onTrue)
                            {
                                if (item is TInterrupt tinter)
                                {
                                    if (tinter.action == TInterrupt.Action.SKIP_RECORD)
                                    {
                                        Console.WriteLine(String.Format("Пропуск записи по условию: значение в ячейке x={0} равно {1}", cond.x, cond.mustBe));
                                        skipRecord = true;
                                    }
                                    if (tinter.action == TInterrupt.Action.STOP_LOOP)
                                    {
                                        Console.WriteLine(String.Format("Выход из цикла по условию: значение в ячейке x={0} равно {1}", cond.x, cond.mustBe));
                                        stopLoop = true;
                                    }
                                    continue;
                                }
                                if (item is TVariable var)
                                {
                                    exception_var = var;
                                    var.Set(tmp[i, var.x]);
                                    stepScope[var.name] = var;
                                    continue;
                                }
                            }
                        }
                        else
                        {
                            foreach (TAction item in cond.onFalse)
                            {
                                if (item is TInterrupt tinter)
                                {
                                    if (tinter.action == TInterrupt.Action.SKIP_RECORD)
                                    {
                                        Console.WriteLine(String.Format("Пропуск записи по условию: значение в ячейке x={0} равно {1}", cond.x, cond.mustBe));
                                        skipRecord = true;
                                    }
                                    if (tinter.action == TInterrupt.Action.STOP_LOOP)
                                    {
                                        Console.WriteLine(String.Format("Выход из цикла по условию: значение в ячейке x={0} равно {1}", cond.x, cond.mustBe));
                                        stopLoop = true;
                                    }
                                    continue;
                                }
                                if (item is TVariable var)
                                {
                                    exception_var = var;
                                    var.Set(tmp[i, var.x]);
                                    stepScope[var.name] = var;
                                    continue;
                                }
                            }
                        }
                    }

                    total++;

                    if (total > maxY - startY)
                    {
                        Logger.instance.log("Попытка выйти за пределы документа, выход из цикла");
                        EOF = true;
                        break;
                    }

                    if (stopLoop)
                    {
                        Logger.instance.log("Выход из цикла по условию");
                        EOF = true;
                        break;
                    }

                    if (skipRecord) continue;

                    foreach (var var in dynamicVars.Values)
                    {
                        exception_var = var;
                        var.Set(tmp[i, var.x]);
                        stepScope[var.name] = var;
                    }

                    callback(stepScope);
                    guiCallback?.Invoke(total);
                }
                watch.Stop();
                Logger.instance.log(String.Format("Сегмент в {0} элементов (с {1} по {2}) обработан за {3} мс", buffer, begin, end, watch.ElapsedMilliseconds));

                begin += buffer;
                end += buffer;
            }
            watchTotal.Stop();
            Logger.instance.log("Total time: " + watchTotal.ElapsedMilliseconds);
            Logger.instance.log("Rows iterated: " + total);
            Logger.instance.log("Buffer size:" + buffer);
        }

        protected void FinalChecks()
        {
            int num = 1;

            if (validators == null) return;
            foreach (var validate in validators)
            {
                stepScope.TryGetValue(validate.var1, out TVariable var1);
                stepScope.TryGetValue(validate.var2, out TVariable var2);

                string value1 = var1?.value?.ToString() ?? "[неизвестно]";
                string value2 = var2?.value?.ToString() ?? "[неизвестно]";

                var elemMsg = validate.Message;
                string message = "";

                if (elemMsg == null)
                {
                    message = string.Format("Финальная проверка №{0} провалена!", num);
                }
                else
                {
                    message = string.Format(elemMsg, value1, value2, num);
                    message = message.Replace("\\n", "\n");
                }

                if (var1 == null || var2 == null || var1.value == null || var2.value == null) throw new Exception(message);

                Logger.instance.log(string.Format(
                    "Проверка номер {0} : {1}({2}) сравнивается с {3}({4})",
                    num, var1 != null ? var1.name : "null", value1, var2 != null ? var2.name : "null", value2));

                bool isEqual = false;
                if (validate.Math != null)
                {
                    int count = validate.Math.count;
                    float prec = Single.Parse(validate.Math.precision);

                    float allowed_precision = (prec / count) * total;
                    float var1fl = Convert.ToSingle(var1.value);
                    float var2fl = Convert.ToSingle(var2.value);

                    Logger.instance.log("var1 = " + var1fl.ToString("G9"));
                    Logger.instance.log("var2 = " + var2fl.ToString("G9"));

                    if (Equals(var1fl, var2fl)) isEqual = true;
                    else
                    {
                        float diff = Math.Abs(Math.Abs(var1fl) - Math.Abs(var2fl));
                        isEqual = diff < allowed_precision;

                        string message_diff = string.Format(validate.Math.message, allowed_precision, diff).Replace("\\n","\n");
                        message += "\n" + message_diff;
                        Logger.instance.log(message_diff);
                    }
                }
                else isEqual = var1.value.Equals(var2.value);

                if (!isEqual) throw new Exception(message);

                num++;
            }
        }

        protected void InitVariables(Xml_Form lForm)
        {
            foreach (var xmlelem in lForm.Fields.IF)
            {
                XElement xelem = XElement.Parse(xmlelem.OuterXml);
                conditions.Add(ScanCondition(xelem));
            }

            foreach (var xmlelem in lForm.Fields.Static)
            {
                XElement xelem = XElement.Parse(xmlelem.OuterXml);
                AddVar(staticVars, getVar(xelem, false));
            }

            foreach (var xmlelem in lForm.Fields.Dynamic)
            {
                XElement xelem = XElement.Parse(xmlelem.OuterXml);
                AddVar(dynamicVars, getVar(xelem, true));
            }
        }

        protected TCondition ScanCondition(XElement xml)
        {
            if (xml.Attribute("X") == null) throw new NullReferenceException("Attribute \"X\" can't be null!");
            if (xml.Attribute("VALUE") == null) throw new NullReferenceException("Attribute \"VALUE\" can't be null!");
            if (xml.Element("THEN") == null) throw new NullReferenceException("Element <THEN> can't be null!");

            TCondition condition = new TCondition
            {
                x = Int32.Parse(xml.Attribute("X").Value),
                mustBe = xml.Attribute("VALUE").Value
            };

            foreach (XElement elem in xml.Element("THEN").Elements())
            {
                TAction action = null;
                if (elem.Name == "SKIP_RECORD")
                    action = new TInterrupt(TInterrupt.Action.SKIP_RECORD);
                if (elem.Name == "STOP_LOOP")
                    action = new TInterrupt(TInterrupt.Action.STOP_LOOP);
                if (elem.Name == "Dynamic")
                    action = getVar(elem, true);
                if (action != null) condition.onTrue.Add(action);
            }

            if (xml.Element("ELSE") != null)
            {
                foreach (XElement elem in xml.Element("ELSE").Elements())
                {
                    TAction action = null;
                    if (elem.Name == "SKIP_RECORD")
                        action = new TInterrupt(TInterrupt.Action.SKIP_RECORD);
                    if (elem.Name == "STOP_LOOP")
                        action = new TInterrupt(TInterrupt.Action.STOP_LOOP);
                    if (elem.Name == "Dynamic")
                        action = getVar(elem, true);
                    if (action != null) condition.onFalse.Add(action);
                }
            }
            return condition;
        }

        protected void AddVar(IDictionary<string, TVariable> dictionary, TVariable variable)
        {
            dictionary.Add(variable.name, variable);
        }

        protected TVariable getVar(XElement xml, bool dynamic)
        {
            var name = xml.Attribute("name").Value;
            var ctype = xml.Attribute("type")?.Value ?? "string";

            TVariable.Type type = TVariable.getByString(ctype);
            TVariable variable;
            switch (type)
            {
                case TVariable.Type.ENumeric:
                    variable = new TNumeric(name);
                    break;
                case TVariable.Type.EDate:
                    variable = new TDate(name);
                    break;
                default:
                    variable = new TVariable(name);
                    break;
            }

            variable.x = Int32.Parse(xml.Attribute("X").Value);
            if (!dynamic) variable.y = Int32.Parse(xml.Attribute("Y").Value);
            variable.dynamic = dynamic;
            variable.type = type;

            if (variable is TNumeric tnumeric)
            {
                if (xml.Attribute("function") != null)
                    tnumeric.function = TNumeric.getFuncByString(xml.Attribute("function").Value);
            }

            if (variable is TDate tdate)
            {
                if (xml.Attribute("lastday") != null)
                    tdate.lastday = Boolean.Parse(xml.Attribute("lastday").Value);
                if (xml.Attribute("language") != null)
                    tdate.language = xml.Attribute("language").Value;
                if (xml.Attribute("format") != null)
                    tdate.format = xml.Attribute("format").Value;
            }

            var regex_pattern = xml.Attribute("regex_pattern");
            if (regex_pattern != null)
            {
                variable.use_regex = true;
                variable.regex_pattern = new Regex(regex_pattern.Value, RegexOptions.Compiled);
                variable.regex_group = xml.Attribute("regex_group") != null ? Int32.Parse(xml.Attribute("regex_group").Value) : 1;
            }
            return variable;
        }
    }
}
