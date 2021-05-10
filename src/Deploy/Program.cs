using Amazon.CDK;

namespace Poc
{
    sealed class Program
    {
        public static void Main(string[] args)
        {
            var app = new App();
            new PocStack(app, "PocStack");

            app.Synth();
        }
    }
}
