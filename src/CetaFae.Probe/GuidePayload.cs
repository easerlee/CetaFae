using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CetaFae.Plugins;

namespace CetaFae.Probe;

public sealed record GuideScopeAnchor(string Name, string ControlType, Rect Bounds, int[] RuntimeId);
public sealed record GuideScope(Rect Region, GuideScopeAnchor? Anchor, bool UserSelected = false)
{
    public static GuideScope FromUserSelection(Rect frame, Rect selection)
    {
        if (!Valid(frame) || !Valid(selection) || !frame.Contains(selection) || selection.Width < 8 || selection.Height < 8)
            throw new InvalidDataException("scope_unavailable");
        var region = new Rect(Math.Floor(selection.Left), Math.Floor(selection.Top),
            Math.Ceiling(selection.Right) - Math.Floor(selection.Left), Math.Ceiling(selection.Bottom) - Math.Floor(selection.Top));
        if (!frame.Contains(region)) throw new InvalidDataException("scope_unavailable");
        return new GuideScope(region, null, true);
    }

    public static GuideScope Resolve(Rect frame, ObservationScopeRule rule, IReadOnlyList<GuideScopeAnchor> anchors)
    {
        var matches = anchors.Where(a => rule.MatchesAnchor(a.Name, a.ControlType)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("scope_unavailable");
        var anchor = matches[0];
        GuidePrivacy.EnsureTextSafe(anchor.Name);
        if (!Valid(frame) || !Valid(anchor.Bounds) || !frame.Contains(anchor.Bounds) ||
            anchor.Name.Length > 2000 || anchor.RuntimeId is not { Length: > 0 })
            throw new InvalidDataException("scope_unavailable");
        var region = anchor.Bounds;
        region.Inflate(rule.Padding, rule.Padding);
        region.Intersect(frame);
        // Integer pixel bounds keep the crop and transmitted geometry on one coordinate system.
        region = new Rect(Math.Floor(region.Left), Math.Floor(region.Top),
            Math.Ceiling(region.Right) - Math.Floor(region.Left), Math.Ceiling(region.Bottom) - Math.Floor(region.Top));
        if (!Valid(region) || !frame.Contains(region) || !region.Contains(anchor.Bounds) ||
            region.Width * region.Height > frame.Width * frame.Height * .8)
            throw new InvalidDataException("scope_unavailable");
        return new GuideScope(region, anchor);
    }

    internal static bool Valid(Rect rect) => !rect.IsEmpty && rect.Width >= 1 && rect.Height >= 1 &&
        new[] { rect.X, rect.Y, rect.Width, rect.Height, rect.Right, rect.Bottom }.All(double.IsFinite);

    public bool HasSameAnchor(GuideScope? other) => other is not null && Region == other.Region && UserSelected == other.UserSelected &&
        (UserSelected ? Anchor is null && other.Anchor is null : Anchor is { } anchor && other.Anchor is { } current &&
        anchor.Name == current.Name && anchor.ControlType == current.ControlType &&
        anchor.Bounds == current.Bounds && anchor.RuntimeId.SequenceEqual(current.RuntimeId));
}

/// <summary>Only this reduced view is used for outbound model requests; local context remains intact.</summary>
public sealed record GuidePayload(IReadOnlyList<GuideCandidate> Candidates, byte[]? Png, string? VisibleContext)
{
    public static GuidePayload From(GuideObservation observation)
    {
        if (observation.ScopeIntent is not null && observation.Scope is null)
            throw new InvalidDataException("scope_unavailable");
        var region = observation.Scope?.Region ?? observation.Frame;
        if (!GuideScope.Valid(region) || !observation.Frame.Contains(region)) throw new InvalidDataException("scope_unavailable");
        double scaleX = 1, scaleY = 1;
        if (observation.Png is { Length: > 0 } png)
        {
            using var stream = new MemoryStream(png, false);
            var image = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            scaleX = image.PixelWidth / region.Width;
            scaleY = image.PixelHeight / region.Height;
        }
        var candidates = observation.Candidates.Where(c => GuideScope.Valid(c.Bounds) && region.Contains(c.Bounds))
            .Select(c => c with { Bounds = new Rect((c.Bounds.X - region.X) * scaleX, (c.Bounds.Y - region.Y) * scaleY,
                c.Bounds.Width * scaleX, c.Bounds.Height * scaleY),
                AutomationId = "", RuntimeId = [] }).ToArray();
        return new GuidePayload(candidates, observation.Png, observation.Scope?.Anchor?.Name);
    }
}
