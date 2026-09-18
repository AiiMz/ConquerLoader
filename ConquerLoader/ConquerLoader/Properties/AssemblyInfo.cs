using System.Resources;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// La información general de un ensamblado se controla mediante el siguiente 
// conjunto de atributos. Cambie estos valores de atributo para modificar la información
// asociada con un ensamblado.
// These are what Windows shows in the exe's Properties > Details pane, so they
// follow the file's name rather than the assembly's: the file is EternalAbyss
// .exe (see TargetName in the csproj) and a player checking what they just
// downloaded should be told the same thing twice. The upstream author stays
// credited in the copyright line, which is where a derivative belongs - this
// fork is ConquerLoader with our patcher and our launch path in it, not a
// clean-room launcher.
[assembly: AssemblyTitle("EternalAbyss")]
[assembly: AssemblyDescription("EternalAbyss game launcher")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("EternalAbyss")]
[assembly: AssemblyProduct("EternalAbyss")]
[assembly: AssemblyCopyright("Copyright © EternalAbyss. Based on ConquerLoader by DaRkFoxDeveloper.")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Si establece ComVisible en false, los tipos de este ensamblado no estarán visibles 
// para los componentes COM.  Si es necesario obtener acceso a un tipo en este ensamblado desde 
// COM, establezca el atributo ComVisible en true en este tipo.
[assembly: ComVisible(false)]

// El siguiente GUID sirve como id. de typelib si este proyecto se expone a COM.
[assembly: Guid("af23285a-b4cd-46b1-962d-0cd66a55482b")]

// La información de versión de un ensamblado consta de los cuatro valores siguientes:
//
//      Versión principal
//      Versión secundaria
//      Número de compilación
//      Revisión
//
// Puede especificar todos los valores o usar los valores predeterminados de número de compilación y de revisión
// utilizando el carácter "*", como se muestra a continuación:
// [assembly: AssemblyVersion("1.0.*")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
[assembly: NeutralResourcesLanguage("en")]
