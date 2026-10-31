using System;
namespace SinkLite.Core
{
    internal sealed class MicrophoneState
    {
        private readonly object sync = new object();
        private bool muted, share;
        private int volume = 100;
        private uint revision = 1, acknowledged;
        public readonly struct Snapshot
        {
            public readonly bool Muted, Share;
            public readonly int Volume;
            public readonly uint Revision, Acknowledged;
            public Snapshot(bool m, bool s, int v, uint r, uint a) { Muted=m; Share=s; Volume=v; Revision=r; Acknowledged=a; }
        }
        public Snapshot Current { get { lock(sync) return new Snapshot(muted,share,volume,revision,acknowledged); } }
        public void SetLocal(bool m, int v, bool s) { lock(sync) { muted=m; volume=Math.Clamp(v,0,100); share=s; revision++; } }
        public void ToggleMute() { lock(sync) { muted=!muted; revision++; } }
        public void SetVolume(int v) { lock(sync) { volume=Math.Clamp(v,0,100); revision++; } }
        public void ToggleShare() { lock(sync) { share=!share; revision++; } }
        public void BeginSession() { lock(sync) { acknowledged=0; revision++; } }
        public bool ApplyRemote(uint request, uint expectedRevision, bool m, int v, int mask=3)
        {
            lock(sync) {
                if (request==0 || unchecked((int)(request-acknowledged))<=0) return false;
                acknowledged=request;
                if (v<0 || v>100 || mask<1 || mask>3) return false;
                if (expectedRevision!=revision) {
                    // A stale volume/unmute must not overwrite PC state. A new mute
                    // request still takes effect, without changing the newer volume.
                    if((mask&1)!=0 && m && !muted) { muted=true; revision++; return true; }
                    return false;
                }
                if((mask&1)!=0) muted=m; if((mask&2)!=0) volume=v; revision++; return true;
            }
        }
    }
}
