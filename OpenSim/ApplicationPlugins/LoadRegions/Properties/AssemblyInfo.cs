/// <license>
/// Copyright (c) Contributors, http://opensimulator.org/
/// See CONTRIBUTORS.TXT for a full list of copyright holders.
///
/// Redistribution and use in source and binary forms, with or without
/// modification, are permitted provided that the following conditions are met:
///     * Redistributions of source code must retain the above copyright
///       notice, this list of conditions and the following disclaimer.
///     * Redistributions in binary form must reproduce the above copyright
///       notice, this list of conditions and the following disclaimer in the
///       documentation and/or other materials provided with the distribution.
///     * Neither the name of the OpenSimulator Project nor the
///       names of its contributors may be used to endorse or promote products
///       derived from this software without specific prior written permission.
///
/// THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
/// EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
/// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
/// DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
/// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
/// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
/// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
/// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
/// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
/// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
/// </license>

/// <summary>
/// System Library Using 
/// References First
/// </summary>
using System.Reflection;
using System.Runtime.InteropServices;

/// <summary>
/// Platform Library Using 
/// References 
/// </summary>

/// <summary>
/// Additional Third Party 
/// Library Using References
/// </summary>
using Mono.Addins;

/// <summary>
/// General Information
/// 
/// The general information about an assembly 
/// is controlled through the following set of 
/// attributes.  We change these attribute values 
/// to modify the information associated with 
/// an assembly.
/// </summary>
[assembly: AssemblyTitle("OpenSim.ApplicationPlugins.LoadRegions")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("http://opensimulator.org")]
[assembly: AssemblyProduct("OpenSim")]
[assembly: AssemblyCopyright("Copyright © OpenSimulator.org Developers 2007-2009")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

/// <summary>
/// ComVisible Settings
/// 
/// We set the ComVisible to false in order 
/// to make this assembly invisible to COM
/// components.  If we need to access a type 
/// in this assembly from COM, then we need 
/// to set the ComVisible attribute to true 
/// on that type.
/// </summary>
[assembly: ComVisible(false)]

/// <summary>
/// GUID Information
/// 
/// The following GUID is used for the ID of 
/// the typelib if this project is exposed to 
/// COM.
/// 
/// Each project must have a unique GUID which 
/// you can generate for free via:
/// 
///     https://guidgenerator.com/
/// </summary>
[assembly: Guid("45b979d9-d8d4-42fd-9780-fe9ac7e86cb4")]

/// <summary>
/// Version Information
/// 
/// The version information for an assembly 
/// consists of the following four values:
/// 
///     Major Version
///     Minor Version
///     Build Number
///     Revision
///     
/// We can specifiy all of the values or 
/// we can default the Build and Revision 
/// Numbers by using the '*' as shown below:
/// 
///     [assembly: AssemblyVersion("1.0.*")]
/// </summary>
[assembly: AssemblyVersion("0.8.2.*")]

[assembly: Addin("OpenSim.ApplicationPlugins.LoadRegions", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDependency("OpenSim", OpenSim.VersionInfo.VersionNumber)]
