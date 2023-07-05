using UnityEngine;

namespace RecastSharp
{
    public class BuildContext : Recast.rcContext
    {
        protected override void doLog(Recast.rcLogCategory category, string msg)
        {
            switch (category)
            {
                case Recast.rcLogCategory.RC_LOG_PROGRESS:
                    Debug.Log(msg);
                    break;
                case Recast.rcLogCategory.RC_LOG_WARNING:
                    Debug.LogWarning(msg);
                    break;
                case Recast.rcLogCategory.RC_LOG_ERROR:
                    Debug.LogError(msg);
                    break;
            }
        }
    }
}