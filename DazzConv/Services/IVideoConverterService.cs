using ConverToDazz.Core.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DazzConv.Services
{
    public interface IVideoConverterService
    {
        Task ConvertAsync(
            string inputFile,
            string outputFile,
            double durationSeconds,
            IProgress<int> progress,
            VideoConversionOptions options,
            CancellationToken token);
    }
}