using System;
using System.IO;
using BsDownmap.Engine;

// Test driver. One little-endian input starts with a row count, then feature rows.
internal static class RankerProbe
{
    private static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Provide one tree model file.");
        if (args[0] == "--mix")
        {
            MeasureMix();
            return;
        }
        using (var modelFile = File.OpenRead(args[0]))
        using (var input = new BinaryReader(Console.OpenStandardInput()))
        using (var output = new BinaryWriter(Console.OpenStandardOutput()))
        {
            var model = TreeEnsemble.Load(modelFile);
            int rowCount = input.ReadInt32();
            var row = new double[model.FeatureCount];
            for (int i = 0; i < rowCount; i++)
            {
                for (int j = 0; j < row.Length; j++)
                    row[j] = input.ReadDouble();
                output.Write(model.Probability(row));
            }
        }
    }

    private static void MeasureMix()
    {
        using (var input = new BinaryReader(Console.OpenStandardInput()))
        using (var output = new BinaryWriter(Console.OpenStandardOutput()))
        {
            var samples = new float[input.ReadInt32()];
            var seconds = new double[input.ReadInt32()];
            for (int i = 0; i < samples.Length; i++) samples[i] = input.ReadSingle();
            for (int i = 0; i < seconds.Length; i++) seconds[i] = input.ReadDouble();
            foreach (double score in MixOnsets.Score(samples, seconds))
                output.Write(score);
        }
    }
}
