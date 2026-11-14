using System;
using System.IO;

namespace BsDownmap.Engine
{
    public sealed class TreeEnsemble
    {
        private const uint ModelMagic = 0x31544442; // BDT1
        private readonly double intercept;
        private readonly Node[][] trees;

        public int FeatureCount { get; }

        private TreeEnsemble(int featureCount, double intercept, Node[][] trees)
        {
            FeatureCount = featureCount;
            this.intercept = intercept;
            this.trees = trees;
        }

        public static TreeEnsemble Load(Stream stream)
        {
            using (var input = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                if (input.ReadUInt32() != ModelMagic)
                    throw new InvalidDataException("Unsupported BS Downmapper tree model format.");
                int featureCount = ReadCount(input, 4096, "features");
                double intercept = input.ReadDouble();
                if (!Finite(intercept))
                    throw new InvalidDataException("Invalid tree model intercept.");

                var trees = new Node[ReadCount(input, 10000, "trees")][];
                for (int i = 0; i < trees.Length; i++)
                {
                    var nodes = new Node[ReadCount(input, 65535, "nodes")];
                    for (int j = 0; j < nodes.Length; j++)
                    {
                        int feature = input.ReadInt32();
                        double value = input.ReadDouble();
                        if (double.IsNaN(value) || feature < -1 || feature >= featureCount ||
                            (feature == -1 && !Finite(value)))
                            throw new InvalidDataException("Invalid tree node feature or value.");
                        if (feature == -1)
                        {
                            nodes[j] = new Node(feature, value, 0, 0, false);
                            continue;
                        }

                        int left = input.ReadInt32(), right = input.ReadInt32();
                        byte missingLeft = input.ReadByte();
                        if (left <= j || right <= j || left >= nodes.Length || right >= nodes.Length ||
                            left == right || missingLeft > 1)
                            throw new InvalidDataException("Invalid tree branch.");
                        nodes[j] = new Node(feature, value, left, right, missingLeft == 1);
                    }
                    trees[i] = nodes;
                }
                if (input.BaseStream.ReadByte() != -1)
                    throw new InvalidDataException("Unexpected data after the tree ensemble.");
                return new TreeEnsemble(featureCount, intercept, trees);
            }
        }

        public double Probability(double[] row)
        {
            if (row.Length != FeatureCount)
                throw new ArgumentException("The feature row does not match the tree model.", nameof(row));

            double score = intercept;
            foreach (var tree in trees)
            {
                int index = 0;
                while (tree[index].Feature >= 0)
                {
                    var node = tree[index];
                    double feature = row[node.Feature];
                    bool left = double.IsNaN(feature) ? node.MissingLeft : feature <= node.Value;
                    index = left ? node.Left : node.Right;
                }
                score += tree[index].Value;
            }
            return 1 / (1 + Math.Exp(-score));
        }

        private static int ReadCount(BinaryReader input, int max, string name)
        {
            int count = input.ReadInt32();
            if (count < 1 || count > max)
                throw new InvalidDataException($"Invalid number of {name} in tree model.");
            return count;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private readonly struct Node
        {
            internal readonly int Feature, Left, Right;
            internal readonly double Value;
            internal readonly bool MissingLeft;

            internal Node(int feature, double value, int left, int right, bool missingLeft)
            {
                Feature = feature;
                Value = value;
                Left = left;
                Right = right;
                MissingLeft = missingLeft;
            }
        }
    }
}
