using QuakeSharp.Core.Common;

namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// The enhanced mode's optional smoothing of alias models (design.md §11.6, step 6; decision 64: both off by default):
/// a pose change blended from the pose before over the time since the change before it (at most 0.1 s, QuakeC's
/// animation rate), and a monster that moves in steps (its origin unchanged for a frame or more, then moved: a
/// MOVETYPE_STEP's 10 Hz thinks) drawn moving along the step over the time between its steps (at most 0.1 s). It keeps
/// its own history of each entity (by its slot, anew when the slot's model changes), never anything of the game's, and
/// shows the game's states late by up to that 0.1 s, which is why it is off by default.
/// </summary>
public sealed class EnhancedSmoothing
{
    /// <summary>The longest a blend or a step is drawn over.</summary>
    public const double MaxInterval = 0.1;

    private readonly Dictionary<int, State> states = [];
    private int generation = -1;

    /// <summary>Blend alias models' poses.</summary>
    public bool Poses { get; set; }

    /// <summary>Smooth monsters' steps.</summary>
    public bool Steps { get; set; }

    /// <summary>A snapshot as it arrives, every one in order: the poses and origins its entities have.</summary>
    public void Observe(EnhancedFrame frame)
    {
        if (frame.ModelGeneration != generation)
        {
            states.Clear();
            generation = frame.ModelGeneration;
        }

        var seen = new HashSet<int>();
        foreach (EnhancedEntity e in frame.Entities)
        {
            if (e.Id == EnhancedEntity.Other || e.Model <= 0 || e.Model >= frame.Models.Count
                || frame.Models[e.Model]?.Alias is not { } alias || !seen.Add(e.Id))
            {
                continue;
            }

            int pose = alias.Pose(e.Frame, frame.Time + e.Syncbase);
            if (!states.TryGetValue(e.Id, out State? s) || s.Model != e.Model || frame.Time < s.Seen)
            {
                states[e.Id] = new State
                {
                    Model = e.Model,
                    Pose = pose,
                    PoseFrom = pose,
                    PoseChanged = frame.Time,
                    Origin = e.Origin,
                    StepFrom = e.Origin,
                    Moved = frame.Time,
                    Seen = frame.Time,
                };
                continue;
            }

            if (pose != s.Pose)
            {
                s.PoseInterval = Math.Clamp(frame.Time - s.PoseChanged, 0, MaxInterval);
                s.PoseFrom = s.Pose;
                s.Pose = pose;
                s.PoseChanged = frame.Time;
            }

            if (!Same(e.Origin, s.Origin))
            {
                // a step: the origin had stood still for a snapshot or more before this move
                bool step = s.StillSince < frame.Time && s.Moved < s.StillSince && Near(s.Origin, e.Origin);
                s.StepInterval = step ? Math.Clamp(frame.Time - s.Moved, 0, MaxInterval) : 0;
                s.StepFrom = s.Origin;
                s.Origin = e.Origin;
                s.Moved = frame.Time;
                s.StillSince = double.MaxValue;
            }
            else if (s.StillSince == double.MaxValue)
            {
                s.StillSince = frame.Time;
            }

            s.Seen = frame.Time;
        }
    }

    /// <summary>
    /// The frame to draw with the smoothing applied at its time: alias entities with their pose blend
    /// (<see cref="EnhancedEntity.PoseFrom"/> to the pose, <see cref="EnhancedEntity.PoseBlend"/>), and stepping
    /// monsters part way along their step.
    /// </summary>
    public EnhancedFrame Apply(EnhancedFrame frame)
    {
        if ((!Poses && !Steps) || states.Count == 0)
        {
            return frame;
        }

        var entities = new EnhancedEntity[frame.Entities.Length];
        bool changed = false;
        for (int i = 0; i < entities.Length; i++)
        {
            EnhancedEntity e = frame.Entities[i];
            if (states.TryGetValue(e.Id, out State? s) && s.Model == e.Model)
            {
                if (Poses && s.PoseInterval > 0)
                {
                    float blend = (float)Math.Clamp((frame.Time - s.PoseChanged) / s.PoseInterval, 0, 1);
                    if (blend < 1)
                    {
                        e = e with { PoseFrom = s.PoseFrom, PoseBlend = blend };
                        changed = true;
                    }
                }

                if (Steps && s.StepInterval > 0)
                {
                    double f = Math.Clamp((frame.Time - s.Moved) / s.StepInterval, 0, 1);
                    if (f < 1)
                    {
                        e = e with { Origin = Lerp(s.StepFrom, s.Origin, (float)f) };
                        changed = true;
                    }
                }
            }

            entities[i] = e;
        }

        return changed ? frame.WithEntities(entities) : frame;
    }

    private static bool Same(in Vec3 a, in Vec3 b) => a[0] == b[0] && a[1] == b[1] && a[2] == b[2];

    private static bool Near(in Vec3 a, in Vec3 b) =>
        Math.Abs(a[0] - b[0]) <= EnhancedFrame.MaxMove && Math.Abs(a[1] - b[1]) <= EnhancedFrame.MaxMove && Math.Abs(a[2] - b[2]) <= EnhancedFrame.MaxMove;

    private static Vec3 Lerp(in Vec3 a, in Vec3 b, float f) =>
        new(a[0] + ((b[0] - a[0]) * f), a[1] + ((b[1] - a[1]) * f), a[2] + ((b[2] - a[2]) * f));

    private sealed class State
    {
        public int Model;
        public int Pose, PoseFrom;
        public double PoseChanged, PoseInterval;
        public Vec3 Origin, StepFrom;
        public double Moved, StepInterval;
        public double StillSince = double.MaxValue;
        public double Seen;
    }
}
