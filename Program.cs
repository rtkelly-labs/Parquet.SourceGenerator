using Parquet.Schema;
Field[] fields = [
  new DataField("i", typeof(int)),
  new DataField("s", typeof(string), isNullable:true),
  new DataField("b", typeof(byte[]), isNullable:true),
  new DecimalDataField("d", 18, 6),
  new DateTimeDataField("t1", DateTimeFormat.Impala),
  new DateTimeDataField("t2", DateTimeFormat.DateAndTimeMicros),
  new TimeDataField("tm", TimeUnitPrecision.Millis),
  new TimeDataField("tu", TimeUnitPrecision.Micros),
  new DataField("g", typeof(Guid)),
  new DataField("u", typeof(uint)),
  new DataField("sb", typeof(sbyte)),
];
foreach (var f in fields) Console.WriteLine($"{f.Name}: {((DataField)f).ClrType} nullable={((DataField)f).IsNullable}");
