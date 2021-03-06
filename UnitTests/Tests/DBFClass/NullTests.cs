﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using ExcelToDbf.Sources.Core;
using ExcelToDbf.Sources.Core.Data.Xml;
using ExcelToDbf.Sources.Core.External;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DomofonExcelToDbfTests.Tests.DBFClass
{
    [TestClass]
    [SuppressMessage("ReSharper", "ObjectCreationAsStatement")]
    public class DBFNullTest
    {
        private string dbfFileName;

        [TestInitialize]
        public void Startup()
        {
            Logger.SetLevel(Logger.LogLevel.DEBUG);
            dbfFileName = Path.GetTempFileName();
        }


        [TestMethod]
        public void TestNullFields()
        {
            ExceptionAssert.Throws<ArgumentNullException>(
                () => new DBF(dbfFileName, null, Encoding.UTF8));
        }


        [TestMethod]
        public void TestNullPath()
        {
            ExceptionAssert.Throws<ArgumentNullException>(
                () => new DBF(null, new List<Xml_DbfField>(), Encoding.UTF8));
        }


        [TestMethod]
        public void TestNullEncoding()
        {
            new DBF(dbfFileName, new List<Xml_DbfField>());
        }

        [TestMethod]
        public void TestNullVariable()
        {
            ExceptionAssert.Throws<ArgumentNullException>(
                () => new DBF(dbfFileName, new List<Xml_DbfField> { null }, Encoding.UTF8));

            ExceptionAssert.Throws<ArgumentNullException>( // не хватает type и length
                () => new DBF(dbfFileName, new List<Xml_DbfField>
                    { new Xml_DbfField { name = "test"} }, Encoding.UTF8));

            ExceptionAssert.Throws<ArgumentNullException>( // не хватает length
                () => new DBF(dbfFileName, new List<Xml_DbfField>
                    { new Xml_DbfField { name = "test", type="string"} }, Encoding.UTF8));
        }

    }
}
