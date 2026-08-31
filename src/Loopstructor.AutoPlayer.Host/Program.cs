using Loopstructor.AutoPlayer.Host;
using System.Text;

Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

return await DesktopHostProgram.RunAsync(args);
