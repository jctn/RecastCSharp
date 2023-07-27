using System;
using System.Diagnostics;

namespace RecastSharp
{
    public static partial class Recast
    {
        /// @par
        ///
        /// Allows the formation of walkable regions that will flow over low lying 
        /// objects such as curbs, and up structures such as stairways. 
        /// 
        /// Two neighboring spans are walkable if: <tt>rcAbs(currentSpan.smax - neighborSpan.smax) < waklableClimb</tt>
        /// 
        /// @warning Will override the effect of #rcFilterLedgeSpans.  So if both filters are used, call
        /// #rcFilterLedgeSpans after calling this filter. 
        ///
        /// @see rcHeightfield, rcConfig
        public static void rcFilterLowHangingWalkableObstacles(rcContext ctx, int walkableClimb, rcHeightfield solid)
        {
            Debug.Assert(ctx != null, "rcContext is null");

            ctx.startTimer(rcTimerLabel.RC_TIMER_FILTER_LOW_OBSTACLES);

            int w = solid.width;
            int h = solid.height;

            for (int y = 0; y < h; ++y)
            {
                for (int x = 0; x < w; ++x)
                {
                    rcSpan ps = null;
                    bool previousWalkable = false;
                    byte previousArea = RC_NULL_AREA;

                    for (rcSpan s = solid.spans![x + y * w]; s != null; ps = s, s = s.next)
                    {
                        bool walkable = s.area != RC_NULL_AREA;
                        // If current span is not walkable, but there is walkable
                        // span just below it, mark the span above it walkable too.
                        if (!walkable && previousWalkable)
                        {
                            if (Math.Abs(s.smax - ps.smax) <= walkableClimb)
                            {
                                s.area = previousArea;
                            }
                        }

                        // Copy walkable flag so that it cannot propagate
                        // past multiple non-walkable objects.
                        previousWalkable = walkable;
                        previousArea = s.area;
                    }
                }
            }

            ctx.stopTimer(rcTimerLabel.RC_TIMER_FILTER_LOW_OBSTACLES);
        }

        /// @par
        ///
        /// A ledge is a span with one or more neighbors whose maximum is further away than @p walkableClimb
        /// from the current span's maximum.
        /// This method removes the impact of the overestimation of conservative voxelization 
        /// so the resulting mesh will not have regions hanging in the air over ledges.
        /// 
        /// A span is a ledge if: <tt>rcAbs(currentSpan.smax - neighborSpan.smax) > walkableClimb</tt>
        /// 
        /// @see rcHeightfield, rcConfig
        public static void rcFilterLedgeSpans(rcContext ctx, int walkableHeight, int walkableClimb,
            rcHeightfield solid)
        {
            Debug.Assert(ctx != null, "rcContext is null");

            ctx.startTimer(rcTimerLabel.RC_TIMER_FILTER_BORDER);

            int xSize = solid.width;
            int zSize = solid.height;

            // Mark border spans.
            for (int z = 0; z < zSize; ++z)
            {
                for (int x = 0; x < xSize; ++x)
                {
                    for (rcSpan s = solid.spans![x + z * xSize]; s != null; s = s.next)
                    {
                        // Skip non walkable spans.
                        if (s.area == RC_NULL_AREA)
                        {
                            continue;
                        }

                        int bot = s.smax;
                        int top = s.next?.smin ?? RC_SPAN_MAX_HEIGHT;

                        // Find neighbours minimum height.
                        int minNeighborHeight = RC_SPAN_MAX_HEIGHT;

                        // Min and max height of accessible neighbours.
                        int accessibleNeighborMinHeight = s.smax;
                        int accessibleNeighborMaxHeight = s.smax;

                        for (int direction = 0; direction < 4; ++direction)
                        {
                            int dx = x + rcGetDirOffsetX(direction);
                            int dy = z + rcGetDirOffsetY(direction);
                            // Skip neighbours which are out of bounds.
                            if (dx < 0 || dy < 0 || dx >= xSize || dy >= zSize)
                            {
                                minNeighborHeight = Math.Min(minNeighborHeight, -walkableClimb - bot);
                                continue;
                            }

                            // From minus infinity to the first span.
                            rcSpan neighborSpan = solid.spans[dx + dy * xSize];
                            int neighborBot = -walkableClimb;
                            int neighborTop = neighborSpan?.smin ?? RC_SPAN_MAX_HEIGHT;
                            // Skip neighbour if the gap between the spans is too small.
                            if (Math.Min(top, neighborTop) - Math.Max(bot, neighborBot) > walkableHeight)
                                minNeighborHeight = Math.Min(minNeighborHeight, neighborBot - bot);

                            // Rest of the spans.
                            for (neighborSpan = solid.spans[dx + dy * xSize]; neighborSpan != null; neighborSpan = neighborSpan.next)
                            {
                                neighborBot = neighborSpan.smax;
                                neighborTop = neighborSpan.next?.smin ?? RC_SPAN_MAX_HEIGHT;
                                // Skip neighbour if the gap between the spans is too small.
                                if (Math.Min(top, neighborTop) - Math.Max(bot, neighborBot) > walkableHeight)
                                {
                                    minNeighborHeight = Math.Min(minNeighborHeight, neighborBot - bot);

                                    // Find min/max accessible neighbour height. 
                                    if (Math.Abs(neighborBot - bot) <= walkableClimb)
                                    {
                                        if (neighborBot < accessibleNeighborMinHeight) accessibleNeighborMinHeight = neighborBot;
                                        if (neighborBot > accessibleNeighborMaxHeight) accessibleNeighborMaxHeight = neighborBot;
                                    }
                                }
                            }
                        }

                        // The current span is close to a ledge if the drop to any
                        // neighbour span is less than the walkableClimb.
                        if (minNeighborHeight < -walkableClimb)
                        {
                            s.area = RC_NULL_AREA;
                        }

                        // If the difference between all neighbours is too large,
                        // we are at steep slope, mark the span as ledge.
                        if ((accessibleNeighborMaxHeight - accessibleNeighborMinHeight) > walkableClimb)
                        {
                            s.area = RC_NULL_AREA;
                        }
                    }
                }
            }

            ctx.stopTimer(rcTimerLabel.RC_TIMER_FILTER_BORDER);
        }

        /// @par
        ///
        /// For this filter, the clearance above the span is the distance from the span's 
        /// maximum to the next higher span's minimum. (Same grid column.)
        /// 
        /// @see rcHeightfield, rcConfig
        public static void rcFilterWalkableLowHeightSpans(rcContext ctx, int walkableHeight, rcHeightfield solid)
        {
            Debug.Assert(ctx != null, "rcContext is null");

            ctx.startTimer(rcTimerLabel.RC_TIMER_FILTER_WALKABLE);

            int w = solid.width;
            int h = solid.height;

            // Remove walkable flag from spans which do not have enough
            // space above them for the agent to stand there.
            for (int y = 0; y < h; ++y)
            {
                for (int x = 0; x < w; ++x)
                {
                    for (rcSpan s = solid.spans![x + y * w]; s != null; s = s.next)
                    {
                        int bot = s.smax;
                        int top = s.next?.smin ?? RC_SPAN_MAX_HEIGHT;
                        if ((top - bot) <= walkableHeight)
                        {
                            s.area = RC_NULL_AREA;
                        }
                    }
                }
            }

            ctx.stopTimer(rcTimerLabel.RC_TIMER_FILTER_WALKABLE);
        }
    }
}