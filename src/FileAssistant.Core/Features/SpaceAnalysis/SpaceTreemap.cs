namespace FileAssistant.Core.Features.SpaceAnalysis;

public readonly record struct SpaceTile(int Id, long Bytes, int Grouped, double X, double Y, double Width, double Height);

public static class SpaceTreemap
{
    public static SpaceTile[] Layout(IReadOnlyList<(int Id, long Bytes)> largest, int count, long bytes, double width, double height)
    {
        if (bytes <= 0 || width <= 0 || height <= 0) return [];

        var weights = new List<(int Id, long Bytes, int Grouped)>(largest.Count + 1);
        var remainingBytes = bytes;
        foreach (var item in largest)
        {
            if (item.Bytes * (width * height / bytes) < 640) break;
            weights.Add((item.Id, item.Bytes, 0));
            remainingBytes -= item.Bytes;
        }
        if (remainingBytes > 0) weights.Add((-1, remainingBytes, count - weights.Count));
        weights.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        var output = new SpaceTile[weights.Count];
        var scale = width * height / bytes;
        double x = 0, y = 0;
        for (var first = 0; first < weights.Count;)
        {
            var side = Math.Min(width, height);
            var firstArea = weights[first].Bytes * scale;
            var area = firstArea;
            var last = first + 1;
            while (last < weights.Count)
            {
                var next = weights[last].Bytes * scale;
                var previousMin = weights[last - 1].Bytes * scale;
                if (Worst(firstArea, next, area + next, side) > Worst(firstArea, previousMin, area, side)) break;
                area += next;
                last++;
            }

            var vertical = width >= height;
            var span = area / side;
            var offset = 0d;
            for (var i = first; i < last; i++)
            {
                var length = weights[i].Bytes * scale / span;
                var item = weights[i];
                output[i] = vertical
                    ? new(item.Id, item.Bytes, item.Grouped, x, y + offset, span, length)
                    : new(item.Id, item.Bytes, item.Grouped, x + offset, y, length, span);
                offset += length;
            }
            if (vertical) { x += span; width -= span; }
            else { y += span; height -= span; }
            first = last;
        }
        return output;
    }

    private static double Worst(double largest, double smallest, double sum, double side)
    {
        var lengthSquared = sum * sum / (side * side);
        return Math.Max(largest / lengthSquared, lengthSquared / smallest);
    }
}
