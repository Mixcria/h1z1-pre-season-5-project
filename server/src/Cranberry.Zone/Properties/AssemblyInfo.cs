using System.Runtime.CompilerServices;

// Lane 1D-lite (docs/97): the death-and-victory orchestration lives on ZoneService's own private
// per-connection state, and the only alternative to an internal seam was a test that waits out the
// 15 s lobby timer and the 20 s countdown in real time. ZoneService.TestSession is that seam, and
// it is the only internal type this exposes. Cranberry.Transport already does the same thing.
[assembly: InternalsVisibleTo("Cranberry.Tests")]
