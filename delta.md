| Benchmark | Baseline | Candidate | Mean Δ | Alloc Δ | Verdict |
| --- | ---: | ---: | ---: | ---: | --- |
| AppendBenchmarks.SingleAppend | 1,528,629.3 ns | 1,296,016.8 ns | -15.2% | +0.0% | unchanged |
| AppendBenchmarks.AppendWithConflictDetected | 1,234,654.0 ns | — | — | — | REMOVED |
| AppendBenchmarks.AppendWithDcbCheck | 1,526,120.9 ns | — | — | — | REMOVED |
| BatchAppendBenchmarks.BatchAppend[BatchSize=1000] | 79,981,383.1 ns | — | — | — | REMOVED |
| BatchAppendBenchmarks.BatchAppend[BatchSize=100] | 9,470,954.0 ns | — | — | — | REMOVED |
| BatchAppendBenchmarks.BatchAppend[BatchSize=10] | 2,530,375.1 ns | — | — | — | REMOVED |
| QueryBenchmarks.StreamAllFromZero[StoreSize=1000000] | — | 1,943,266.6 ns | — | — | ADDED |
| QueryBenchmarks.StreamAllFromZero[StoreSize=100000] | — | 1,180,658.6 ns | — | — | ADDED |
| QueryBenchmarks.StreamAllFromZero[StoreSize=10000] | — | 2,031,570.6 ns | — | — | ADDED |
| TagFanOutBenchmarks.AppendWithTagFanOut[TagsPerEvent=1] | 1,298,450.1 ns | — | — | — | REMOVED |
| TagFanOutBenchmarks.AppendWithTagFanOut[TagsPerEvent=20] | 1,619,666.4 ns | — | — | — | REMOVED |
| TagFanOutBenchmarks.AppendWithTagFanOut[TagsPerEvent=5] | 1,583,750.0 ns | — | — | — | REMOVED |
