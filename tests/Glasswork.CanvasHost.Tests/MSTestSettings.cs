using Microsoft.VisualStudio.TestTools.UnitTesting;

// These black-box tests start real child processes and use real loopback HTTP.
// Keep their resource/isolation policy explicit rather than relying on an
// adapter default. This setting is not a diagnosis for any observed failure.
[assembly: DoNotParallelize]
