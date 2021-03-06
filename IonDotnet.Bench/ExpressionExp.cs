﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using IonDotnet.Serialization;
using Newtonsoft.Json;

namespace IonDotnet.Bench
{
    // ReSharper disable once UnusedMember.Global
    public class ExpressionExp : IRunable
    {
        private class Per
        {
            public string Name { get; set; }
            public int Age { get; set; }
            public DateTimeOffset Birth { get; set; }
            public decimal Money { get; set; }
        }

        public void Run(string[] args)
        {
            // var exp = typeof(IEnumerator).GetMethod(nameof(IEnumerator.MoveNext));
            // var exp2 = typeof(IValueWriter).GetMethod(nameof(IIonWriter.WriteString));
            // Console.WriteLine(exp);
            // Console.WriteLine(exp2);
            // return;

            //            var trueS = IonSerialization.Serialize(new Per
            //            {
            //                Name = "huy"
            //            });
            //            Console.WriteLine(string.Join(',', trueS.Select(b => $"{b:x2}")));
            var s = IonSerializerExpression.Serialize(new[]
            {
                new Experiment
                {
                    Name = "Boxing Perftest",
                    // Duration = TimeSpan.FromSeconds(90),
                    Id = 233,
                    StartDate = new DateTimeOffset(2018, 07, 21, 11, 11, 11, TimeSpan.Zero),
                    IsActive = true,
                    Description = "Measure performance impact of boxing",
                    Result = ExperimentResult.Failure,
                    SampleData = new byte[100],
                    Budget = decimal.Parse("12345.01234567890123456789"),
                    Outputs = new[] {1, 2, 3}
                }
            });

            var s2 = IonSerializerExpression.Serialize(new[]
            {
                new Experiment
                {
                    Name = "Boxing Perftest",
                    // Duration = TimeSpan.FromSeconds(90),
                    Id = 233,
                    StartDate = new DateTimeOffset(2018, 07, 21, 11, 11, 11, TimeSpan.Zero),
                    IsActive = true,
                    Description = "Measure performance impact of boxing",
                    Result = ExperimentResult.Failure,
                    SampleData = new byte[100],
                    Budget = decimal.Parse("12345.01234567890123456789")
                }
            });
            //            Console.WriteLine(string.Join(',', s.Select(b => $"{b:x2}")));
            var d = IonSerialization.Deserialize<Experiment[]>(s);
            var json = JsonConvert.SerializeObject(d, Formatting.Indented);
            Console.WriteLine(json);
        }
    }
}
