/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Xml;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Timers;
using Timer = System.Timers.Timer;
using OpenMetaverse;
using log4net;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Client;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes.Animation;
using OpenSim.Region.Framework.Scenes.Types;
using OpenSim.Region.PhysicsModules.SharedBase;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using OpenSim.Services.Interfaces;
using TeleportFlags = OpenSim.Framework.Constants.TeleportFlags;

namespace OpenSim.Region.Framework.Scenes
{
    [Flags]
    enum ScriptControlled : uint
    {
        CONTROL_ZERO = 0,
        CONTROL_FWD = 1,
        CONTROL_BACK = 2,
        CONTROL_LEFT = 4,
        CONTROL_RIGHT = 8,
        CONTROL_UP = 16,
        CONTROL_DOWN = 32,
        CONTROL_ROT_LEFT = 256,
        CONTROL_ROT_RIGHT = 512,
        CONTROL_LBUTTON = 268435456,
        CONTROL_ML_LBUTTON = 1073741824
    }

    struct ScriptControllers
    {
        public UUID objectID;
        public UUID itemID;
        public ScriptControlled ignoreControls;
        public ScriptControlled eventControls;
    }

    public delegate void SendCoarseLocationsMethod(UUID scene, ScenePresence presence, List<Vector3> coarseLocations, List<UUID> avatarUUIDs);

    public class ScenePresence : EntityBase, IScenePresence
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly String LogHeader = "[SCENE PRESENCE]";

//        ~ScenePresence()
//        {
//            m_log.DebugFormat("[SCENE PRESENCE]: Destructor called on {0}", Name);
//        }

        public void TriggerScenePresenceUpdated()
        {
            if (m_scene != null)
                m_scene.EventManager.TriggerScenePresenceUpdated(this);
        }

        public bool isNPC { get; private set; }

        private PresenceType m_presenceType;
        public PresenceType PresenceType {
            get {return m_presenceType;}
            private set
            {
                m_presenceType = value;
                isNPC = (m_presenceType == PresenceType.Npc);
            }
        }

        private ScenePresenceStateMachine m_stateMachine;

        /// <summary>
        /// The current state of this presence.  Governs only the existence lifecycle.  See ScenePresenceStateMachine
        /// for more details.
        /// </summary>
        public ScenePresenceState LifecycleState 
        { 
            get
            {
                return m_stateMachine.GetState();
            }

            set
            {
                m_stateMachine.SetState(value);
            }
        }

        /// <summary>
        /// This exists to prevent race conditions between two CompleteMovement threads if the simulator is slow and
        /// the viewer fires these in quick succession.
        /// </summary>
        /// <remarks>
        /// TODO: The child -> agent transition should be folded into LifecycleState and the CompleteMovement 
        /// regulation done there.
        /// </remarks>
        private object m_completeMovementLock = new object();

//        private static readonly byte[] DEFAULT_TEXTURE = AvatarAppearance.GetDefaultTexture().GetBytes();
        private static readonly Array DIR_CONTROL_FLAGS = Enum.GetValues(typeof(Dir_ControlFlags));
        private static readonly Vector3 HEAD_ADJUSTMENT = new Vector3(0f, 0f, 0.3f);
        
        /// <summary>
        /// Experimentally determined "fudge factor" to make sit-target positions
        /// the same as in SecondLife. Fudge factor was tested for 36 different
        /// test cases including prims of type box, sphere, cylinder, and torus,
        /// with varying parameters for sit target location, prim size, prim
        /// rotation, prim cut, prim twist, prim taper, and prim shear. See mantis
        /// issue #1716
        /// </summary>
        public static readonly Vector3 SIT_TARGET_ADJUSTMENT = new Vector3(0.0f, 0.0f, 0.4f);
        public bool  LegacySitOffsets = true;

        /// <summary>
        /// Movement updates for agents in neighboring regions are sent directly to clients.
        /// This value only affects how often agent positions are sent to neighbor regions
        /// for things such as distance-based update prioritization
        /// this are the square of real distances
        /// </summary>
        public static readonly float MOVEMENT = .25f;
        public static readonly float SIGNIFICANT_MOVEMENT = 16.0f;
        public static readonly float CHILDUPDATES_MOVEMENT = 100.0f;
        public static readonly float CHILDUPDATES_TIME = 10000f; // min time between child updates (ms)

        private UUID m_previusParcelUUID = UUID.Zero;
        private UUID m_currentParcelUUID = UUID.Zero;
        private bool m_previusParcelHide = false;
        private bool m_currentParcelHide = false;
        private object parcelLock = new Object();

        public UUID currentParcelUUID
        {
            get { return m_currentParcelUUID; }
            set
            {
                lock (parcelLock)
                {
                    bool oldhide = m_currentParcelHide;
                    bool checksame = true;
                    if (value != m_currentParcelUUID)
                    {
                        m_previusParcelHide = m_currentParcelHide;
                        m_previusParcelUUID = m_currentParcelUUID;
                        checksame = false;
                    }
                    m_currentParcelUUID = value;
                    m_currentParcelHide = false;

                    ILandObject land = m_scene.LandChannel.GetLandObject(AbsolutePosition.X, AbsolutePosition.Y);
                    if (land != null && !land.LandData.SeeAVs)
                        m_currentParcelHide = true;

                    if (m_previusParcelUUID != UUID.Zero || checksame)
                        ParcelCrossCheck(m_currentParcelUUID,m_previusParcelUUID,m_currentParcelHide, m_previusParcelHide, oldhide,checksame);
                }
            }
        }

        public void sitSOGmoved()
        {
/*
            if (IsDeleted || !IsSatOnObject)
                //what me?  nahh
                return;
            if (IsInTransit)
                return;

            ILandObject land = m_scene.LandChannel.GetLandObject(AbsolutePosition.X, AbsolutePosition.Y);
            if (land == null)
                return; //??
            UUID parcelID = land.LandData.GlobalID;
            if (m_currentParcelUUID != parcelID)
                currentParcelUUID = parcelID;
*/
        }

        public bool ParcelAllowThisAvatarSounds
        {
            get
            {
                try
                {
                    lock (parcelLock)
                    {
                        ILandObject land = m_scene.LandChannel.GetLandObject(AbsolutePosition.X, AbsolutePosition.Y);
                        if (land == null)
                            return true;
                        if (land.LandData.AnyAVSounds)
                            return true;
                        if (!land.LandData.GroupAVSounds)
                            return false;
                        return land.LandData.GroupID == ControllingClient.ActiveGroupId;
                    }
                }
                catch
                {
                    return true;
                }
            }
        }

        public bool ParcelHideThisAvatar
        {
            get
            {
                return m_currentParcelHide;
            }
        }
        
        /// <value>
        /// The animator for this avatar
        /// </value>
        public ScenePresenceAnimator Animator { get; private set; }

        /// <value>
        /// Server Side Animation Override
        /// </value>
        public MovementAnimationOverrides Overrides { get; private set; }
        public String sitAnimation = "SIT";
        /// <summary>
        /// Attachments recorded on this avatar.
        /// </summary>
        /// <remarks>
        /// TODO: For some reason, we effectively have a list both here and in Appearance.  Need to work out if this is
        /// necessary.
        /// </remarks>
        private List<SceneObjectGroup> m_attachments = new List<SceneObjectGroup>();

        public Object AttachmentsSyncLock { get; private set; }

        private Dictionary<UUID, ScriptControllers> scriptedcontrols = new Dictionary<UUID, ScriptControllers>();
        private ScriptControlled IgnoredControls = ScriptControlled.CONTROL_ZERO;
        private ScriptControlled LastCommands = ScriptControlled.CONTROL_ZERO;
        private bool MouseDown = false;
//        private SceneObjectGroup proxyObjectGroup;
        //private SceneObjectPart proxyObjectPart = null;
        public Vector3 lastKnownAllowedPosition;
        public bool sentMessageAboutRestrictedParcelFlyingDown;
        public Vector4 CollisionPlane = Vector4.UnitW;

        private Vector3 m_lastPosition;
        private Quaternion m_lastRotation;
        private Vector3 m_lastVelocity;
        private Vector3 m_lastSize = new Vector3(0.45f,0.6f,1.9f);

        private bool m_followCamAuto = false;

        private Vector3? m_forceToApply;
        private int m_userFlags;
        public int UserFlags
        {
            get { return m_userFlags; }
        }

        // Flying
        public bool Flying
        {
            get { return PhysicsActor != null && PhysicsActor.Flying; }
            set { PhysicsActor.Flying = value; }
        }

         public bool IsColliding
        {
            get { return PhysicsActor != null && PhysicsActor.IsColliding; }
            // We would expect setting IsColliding to be private but it's used by a hack in Scene
            set { PhysicsActor.IsColliding = value; }
        }

//        private int m_lastColCount = -1;		//KF: Look for Collision chnages
//        private int m_updateCount = 0;			//KF: Update Anims for a while
//        private static readonly int UPDATE_COUNT = 10;		// how many frames to update for
        private List<uint> m_lastColliders = new List<uint>();

        private TeleportFlags m_teleportFlags;
        public TeleportFlags TeleportFlags
        {
            get { return m_teleportFlags; }
            set { m_teleportFlags = value; }
        }

        private uint m_requestedSitTargetID;
        private UUID m_requestedSitTargetUUID;

        /// <summary>
        /// Are we sitting on the ground?
        /// </summary>
        public bool SitGround { get; private set; }

        private SendCoarseLocationsMethod m_sendCoarseLocationsMethod;

        //private Vector3 m_requestedSitOffset = new Vector3();

        private Vector3 m_LastFinitePos;

        private float m_sitAvatarHeight = 2.0f;

        private bool childUpdatesBusy = false;
        private int lastChildUpdatesTime;
        private Vector3 m_lastChildAgentUpdatePosition;
//        private Vector3 m_lastChildAgentUpdateCamPosition;

        private const int LAND_VELOCITYMAG_MAX = 12;

        private const float FLY_ROLL_MAX_RADIANS = 1.1f;

        private const float FLY_ROLL_RADIANS_PER_UPDATE = 0.06f;
        private const float FLY_ROLL_RESET_RADIANS_PER_UPDATE = 0.02f;

        private float m_health = 100f;

        protected ulong crossingFromRegion;

        private readonly Vector3[] Dir_Vectors = new Vector3[12];

        protected int  m_reprioritizationLastTime;
        protected bool m_reprioritizationBusy;
        protected Vector3 m_reprioritizationLastPosition;
        
        private Quaternion m_headrotation = Quaternion.Identity;

        //PauPaw:Proper PID Controler for autopilot************
        public bool MovingToTarget { get; private set; }
        public Vector3 MoveToPositionTarget { get; private set; }

        /// <summary>
        /// Controls whether an avatar automatically moving to a target will land when it gets there (if flying).
        /// </summary>
        public bool LandAtTarget { get; private set; }

        private int m_movementUpdateCount;
        private const int NumMovementsBetweenRayCast = 5;

        private bool CameraConstraintActive;
        //private int m_moveToPositionStateStatus;
        //*****************************************************

        //private bool m_collisionEventFlag = false;
        private object m_collisionEventLock = new Object();
		
        private int m_movementAnimationUpdateCounter = 0;

        public Vector3 PrevSitOffset { get; set; }

        protected AvatarAppearance m_appearance;

        public AvatarAppearance Appearance
        {
            get { return m_appearance; }
            set
            {
                m_appearance = value;
//                m_log.DebugFormat("[SCENE PRESENCE]: Set appearance for {0} to {1}", Name, value);
            }
        }


        /// <summary>
        /// Copy of the script states while the agent is in transit. This state may
        /// need to be placed back in case of transfer fail.
        /// </summary>
        public List<string> InTransitScriptStates
        {
            get { return m_InTransitScriptStates; }
            private set { m_InTransitScriptStates = value; }
        }
        private List<string> m_InTransitScriptStates = new List<string>();

        /// <summary>
        /// Implemented Control Flags
        /// </summary>
        private enum Dir_ControlFlags:uint
        {
            DIR_CONTROL_FLAG_FORWARD = AgentManager.ControlFlags.AGENT_CONTROL_AT_POS,
            DIR_CONTROL_FLAG_BACK = AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG,
            DIR_CONTROL_FLAG_LEFT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS,
            DIR_CONTROL_FLAG_RIGHT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG,
            DIR_CONTROL_FLAG_UP = AgentManager.ControlFlags.AGENT_CONTROL_UP_POS,
            DIR_CONTROL_FLAG_DOWN = AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG,
            DIR_CONTROL_FLAG_FORWARD_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_POS,
            DIR_CONTROL_FLAG_BACKWARD_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_NEG,
            DIR_CONTROL_FLAG_LEFT_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_POS,
            DIR_CONTROL_FLAG_RIGHT_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_NEG,
            DIR_CONTROL_FLAG_UP_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_POS,
            DIR_CONTROL_FLAG_DOWN_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG
        }
        
        /// <summary>
        /// Position at which a significant movement was made
        /// </summary>
        private Vector3 posLastSignificantMove;
        private Vector3 posLastMove;

        #region For teleports and crossings callbacks

        /// <summary>
        /// In the V1 teleport protocol, the destination simulator sends ReleaseAgent to this address.
        /// </summary>
        private string m_callbackURI;

        /// <summary>
        /// Records the region from which this presence originated, if not from login.
        /// </summary>
        /// <remarks>
        /// Also acts as a signal in the teleport V2 process to release UpdateAgent after a viewer has triggered
        /// CompleteMovement and made the previous child agent a root agent.
        /// </remarks>
        private UUID m_originRegionID;

        /// <summary>
        /// This object is used as a lock before accessing m_originRegionID to make sure that every thread is seeing
        /// the very latest value and not using some cached version.  Cannot make m_originRegionID itself volatite as
        /// it is a value type.
        /// </summary>
        private object m_originRegionIDAccessLock = new object();

        /// <summary>
        /// Used by the entity transfer module to signal when the presence should not be closed because a subsequent
        /// teleport is reusing the connection.
        /// </summary>
        /// <remarks>May be refactored or move somewhere else soon.</remarks>
        public bool DoNotCloseAfterTeleport { get; set; }

        #endregion

        /// <value>
        /// Script engines present in the scene
        /// </value>
        private IScriptModule[] m_scriptEngines;

        private enum LandingPointBehavior
        {
            OS = 1,
            SL = 2
        }

        private LandingPointBehavior m_LandingPointBehavior = LandingPointBehavior.OS;

        #region Properties

        /// <summary>
        /// Physical scene representation of this Avatar.
        /// </summary>
        public PhysicsActor PhysicsActor { get; private set; }

        /// <summary>
        /// Record user movement inputs.
        /// </summary>
        public uint MovementFlag { get; private set; }

        /// <summary>
        /// Is the agent stop control flag currently active?
        /// </summary>
        public bool AgentControlStopActive { get; private set; }

        private bool m_invulnerable = true;

        public bool Invulnerable
        {
            set { m_invulnerable = value; }
            get { return m_invulnerable; }
        }

        private int m_userLevel;

        public int UserLevel
        {
            get { return m_userLevel; }
            private set { m_userLevel = value; }
        }

        private int m_godLevel;

        public int GodLevel
        {
            get { return m_godLevel; }
            private set { m_godLevel = value; }
        }

        private ulong m_rootRegionHandle;
        private Vector3 m_rootRegionPosition = new Vector3();

        public ulong RegionHandle
        {
            get { return m_rootRegionHandle; }
            private set
            {
                m_rootRegionHandle = value;
                // position rounded to lower multiple of 256m
                m_rootRegionPosition.X = (float)((m_rootRegionHandle >> 32) & 0xffffff00);
                m_rootRegionPosition.Y = (float)(m_rootRegionHandle & 0xffffff00); 
            }
        }

        #region Client Camera

        /// <summary>
        /// Position of agent's camera in world (region cordinates)
        /// </summary>
//        protected Vector3 m_lastCameraPosition;

        private Vector4 m_lastCameraCollisionPlane = new Vector4(0f, 0f, 0f, 1);
        private bool m_doingCamRayCast = false;

        public Vector3 CameraPosition { get; set; }

        public Quaternion CameraRotation
        {
            get { return Util.Axes2Rot(CameraAtAxis, CameraLeftAxis, CameraUpAxis); }
        }

        // Use these three vectors to figure out what the agent is looking at
        // Convert it to a Matrix and/or Quaternion
        //
        public Vector3 CameraAtAxis { get; set; }
        public Vector3 CameraLeftAxis { get; set; }
        public Vector3 CameraUpAxis { get; set; }

        public Vector3 Lookat
        {
            get
            {
                Vector3 a = new Vector3(CameraAtAxis.X, CameraAtAxis.Y, 0);
                a.Normalize();
                return a;
            }
        }
        #endregion        

        public string Firstname { get; private set; }
        public string Lastname { get; private set; }

        public string Grouptitle
        {
            get { return UseFakeGroupTitle ? "(Loading)" : m_groupTitle; }
            set { m_groupTitle = value; }
        }
        private string m_groupTitle;

        /// <summary>
        /// When this is 'true', return a dummy group title instead of the real group title. This is
        /// used as part of a hack to force viewers to update the displayed avatar name.
        /// </summary>
        public bool UseFakeGroupTitle { get; set; }

        // Agent's Draw distance.
        private float m_drawDistance = 255f;
        public float DrawDistance
        {
            get
            {
            return m_drawDistance;
            }
            set
            {
            m_drawDistance = Util.Clamp(value, 32f, m_scene.MaxDrawDistance);
            }
        }

        public float RegionViewDistance
        {
            get
            {
            return Util.Clamp(m_drawDistance, 32f, m_scene.MaxRegionViewDistance);
            }
         }

        public bool AllowMovement { get; set; }

        private bool m_setAlwaysRun;
        
        public bool SetAlwaysRun
        {
            get
            {
                if (PhysicsActor != null)
                {
                    return PhysicsActor.SetAlwaysRun;
                }
                else
                {
                    return m_setAlwaysRun;
                }
            }
            set
            {
                m_setAlwaysRun = value;
                if (PhysicsActor != null)
                {
                    PhysicsActor.SetAlwaysRun = value;
                }
            }
        }


        public byte State { get; set; }

        private AgentManager.ControlFlags m_AgentControlFlags;

        public uint AgentControlFlags
        {
            get { return (uint)m_AgentControlFlags; }
            set { m_AgentControlFlags = (AgentManager.ControlFlags)value; }
        }

        public IClientAPI ControllingClient { get; set; }

        public IClientCore ClientView
        {
            get { return (IClientCore)ControllingClient; }
        }

        public UUID COF { get; set; }

//        public Vector3 ParentPosition { get; set; }

        /// <summary>
        /// Position of this avatar relative to the region the avatar is in
        /// </summary>
        public override Vector3 AbsolutePosition
        {
            get
            {
                if (PhysicsActor != null)
                {
                    m_pos = PhysicsActor.Position;

//                    m_log.DebugFormat(
//                        "[SCENE PRESENCE]: Set position of {0} in {1} to {2} via getting AbsolutePosition!",
//                        Name, Scene.Name, m_pos);
                }
                else
                {
//                    m_log.DebugFormat("[SCENE PRESENCE]: Fetching abs pos where PhysicsActor == null and parent part {0} for {1}", Name, Scene.Name);
                    // Obtain the correct position of a seated avatar.
                    // In addition to providing the correct position while
                    // the avatar is seated, this value will also
                    // be used as the location to unsit to.
                    //
                    // If ParentID is not 0, assume we are a seated avatar
                    // and we should return the position based on the sittarget
                    // offset and rotation of the prim we are seated on.
                    //
                    // Generally, m_pos will contain the position of the avatar
                    // in the sim unless the avatar is on a sit target. While
                    // on a sit target, m_pos will contain the desired offset
                    // without the parent rotation applied.
                    if (ParentPart != null)
                    {
                        SceneObjectPart rootPart = ParentPart.ParentGroup.RootPart;
                        //                    if (sitPart != null)
                        //                        return sitPart.AbsolutePosition + (m_pos * sitPart.GetWorldRotation());
                        if (rootPart != null)
                            return rootPart.AbsolutePosition + (m_pos * rootPart.GetWorldRotation());
                    }
                }
                
                return m_pos;
            }
            set
            {
//                m_log.DebugFormat("[SCENE PRESENCE]: Setting position of {0} to {1} in {2}", Name, value, Scene.Name);
//                Util.PrintCallStack();

                if (PhysicsActor != null)
                {
                    try
                    {
                        PhysicsActor.Position = value;
                    }
                    catch (Exception e)
                    {
                        m_log.Error("[SCENE PRESENCE]: ABSOLUTE POSITION " + e.Message);
                    }
                }

                // Don't update while sitting.  The PhysicsActor above is null whilst sitting.
                if (ParentID == 0)
                    m_pos = value;

                //m_log.DebugFormat(
                //    "[ENTITY BASE]: In {0} set AbsolutePosition of {1} to {2}",
                //    Scene.RegionInfo.RegionName, Name, m_pos);
                TriggerScenePresenceUpdated();
            }
        }

        /// <summary>
        /// If sitting, returns the offset position from the prim the avatar is sitting on.
        /// Otherwise, returns absolute position in the scene.
        /// </summary>
        public Vector3 OffsetPosition
        {
            get { return m_pos; }
            // Don't remove setter. It's not currently used in core but
            // upcoming Avination code needs it.
            set
            {
                // There is no offset position when not seated
                if (ParentID == 0)
                    return;

                m_pos = value;
                TriggerScenePresenceUpdated();
            }
        }

        /// <summary>
        /// Current velocity of the avatar.
        /// </summary>
        public override Vector3 Velocity
        {
            get
            {
                if (PhysicsActor != null)
                {
                    m_velocity = PhysicsActor.Velocity;

//                    m_log.DebugFormat(
//                        "[SCENE PRESENCE]: Set velocity {0} for {1} in {2} via getting Velocity!",
//                        m_velocity, Name, Scene.RegionInfo.RegionName);
                }

                return m_velocity;
            }

            set
            {
                if (PhysicsActor != null)
                {
                    try
                    {
                        PhysicsActor.TargetVelocity = value;
                    }
                    catch (Exception e)
                    {
                        m_log.Error("[SCENE PRESENCE]: VELOCITY " + e.Message);
                    }
                }

                m_velocity = value;

//                m_log.DebugFormat(
//                    "[SCENE PRESENCE]: In {0} set velocity of {1} to {2}",
//                    Scene.RegionInfo.RegionName, Name, m_velocity);                                       
            }
        }

        private Quaternion m_bodyRot = Quaternion.Identity;

        /// <summary>
        /// The rotation of the avatar.
        /// </summary>
        /// <remarks>
        /// If the avatar is not sitting, this is with respect to the world
        /// If the avatar is sitting, this is a with respect to the part that it's sitting upon (a local rotation).
        /// If you always want the world rotation, use GetWorldRotation()
        /// </remarks>
        public Quaternion Rotation
        {
            get 
            { 
                return m_bodyRot; 
            }

            set
            {
                m_bodyRot = value;

                if (PhysicsActor != null)
                {
                    try
                    {
                        PhysicsActor.Orientation = m_bodyRot;
                    }
                    catch (Exception e)
                    {
                        m_log.Error("[SCENE PRESENCE]: Orientation " + e.Message);
                    }
                }
//                m_log.DebugFormat("[SCENE PRESENCE]: Body rot for {0} set to {1}", Name, m_bodyRot);
            }
        }

        // Used for limited viewer 'fake' user rotations.
        private Vector3 m_AngularVelocity = Vector3.Zero;

        public Vector3 AngularVelocity
        {
            get { return m_AngularVelocity; }
        }

        public bool IsChildAgent { get; set; }
        public bool IsLoggingIn { get; set; }

        /// <summary>
        /// If the avatar is sitting, the local ID of the prim that it's sitting on.  If not sitting then zero.
        /// </summary>
        public uint ParentID { get; set; }

        public UUID ParentUUID
        {
            get { return m_parentUUID; }
            set { m_parentUUID = value; }
        }
        private UUID m_parentUUID = UUID.Zero;

        /// <summary>
        /// Are we sitting on an object?
        /// </summary>
        /// <remarks>A more readable way of testing presence sit status than ParentID == 0</remarks>
        public bool IsSatOnObject { get { return ParentID != 0; } }

        /// <summary>
        /// If the avatar is sitting, the prim that it's sitting on.  If not sitting then null.
        /// </summary>
        /// <remarks>
        /// If you use this property then you must take a reference since another thread could set it to null.
        /// </remarks>
        public SceneObjectPart ParentPart { get; set; }

        public float Health
        {
            get { return m_health; }
            set { m_health = value; }
        }

        /// <summary>
        /// Gets the world rotation of this presence.
        /// </summary>
        /// <remarks>
        /// Unlike Rotation, this returns the world rotation no matter whether the avatar is sitting on a prim or not.
        /// </remarks>
        /// <returns></returns>
        public Quaternion GetWorldRotation()
        {
            if (IsSatOnObject)
            {
                SceneObjectPart sitPart = ParentPart;

                if (sitPart != null)
                    return sitPart.GetWorldRotation() * Rotation;
            }

            return Rotation;
        }

        /// <summary>
        /// Get velocity relative to the world.
        /// </summary>
        public Vector3 GetWorldVelocity()
        {
            SceneObjectPart sitPart = ParentPart;

            if (sitPart != null)
                return sitPart.ParentGroup.Velocity;

            return Velocity;
        }

        public void AdjustKnownSeeds()
        {
            Dictionary<ulong, string> seeds;

            if (Scene.CapsModule != null)
                seeds = Scene.CapsModule.GetChildrenSeeds(UUID);
            else
                seeds = new Dictionary<ulong, string>();

/* we can't do this anymore
            List<ulong> old = new List<ulong>();
            foreach (ulong handle in seeds.Keys)
            {
                uint x, y;
                Util.RegionHandleToRegionLoc(handle, out x, out y);
//                if (Util.IsOutsideView(DrawDistance, x, Scene.RegionInfo.RegionLocX, y, Scene.RegionInfo.RegionLocY,))
                {
                    old.Add(handle);
                }
            }

            DropOldNeighbours(old);
            
            if (Scene.CapsModule != null)
                Scene.CapsModule.SetChildrenSeed(UUID, seeds);
*/            
            KnownRegions = seeds;
            //m_log.Debug(" ++++++++++AFTER+++++++++++++ ");
            //DumpKnownRegions();
        }

        public void DumpKnownRegions()
        {
            m_log.Info("================ KnownRegions "+Scene.RegionInfo.RegionName+" ================");
            foreach (KeyValuePair<ulong, string> kvp in KnownRegions)
            {
                uint x, y;
                Util.RegionHandleToRegionLoc(kvp.Key, out x, out y);
                m_log.Info(" >> "+x+", "+y+": "+kvp.Value);
            }
        }

        private bool m_mouseLook;
        private bool m_leftButtonDown;

        private bool m_inTransit;

        /// <summary>
        /// This signals whether the presence is in transit between neighbouring regions.
        /// </summary>
        /// <remarks> 
        /// It is not set when the presence is teleporting or logging in/out directly to a region.
        /// </remarks>
        public bool IsInTransit
        {
            get { return m_inTransit; }
            set { 
                if(value)
                {
                    if (Flying)
                        m_AgentControlFlags |= AgentManager.ControlFlags.AGENT_CONTROL_FLY;
                    else
                        m_AgentControlFlags &= ~AgentManager.ControlFlags.AGENT_CONTROL_FLY;
                }
                m_inTransit = value;
            }
        }


        /// <summary>
        /// Modifier for agent movement if we get an AGENT_CONTROL_STOP whilst walking or running
        /// </summary>
        /// <remarks>
        /// AGENT_CONTRL_STOP comes about if user holds down space key on viewers.
        /// </remarks>
        private const float AgentControlStopSlowVel = 0.2f;
        // velocities
        public const float AgentControlNudgeVel = 1.0f; // setting this diferent from normal as no effect currently
        public const float AgentControlNormalVel = 1.0f;

        // old normal speed was tuned to match sl normal plus Fast modifiers
        // so we need to rescale it
        private float m_speedModifier = 1.0f;

        public float SpeedModifier
        {
            get { return m_speedModifier; }
            set { m_speedModifier = value; }
        }

        private bool m_forceFly;

        public bool ForceFly
        {
            get { return m_forceFly; }
            set { m_forceFly = value; }
        }

        private bool m_flyDisabled;

        public bool FlyDisabled
        {
            get { return m_flyDisabled; }
            set { m_flyDisabled = value; }
        }

        public string Viewer
        {
            get { return Util.GetViewerName(m_scene.AuthenticateHandler.GetAgentCircuitData(ControllingClient.CircuitCode)); }
        }
 
        #endregion

        #region Constructor(s)

        public ScenePresence(
            IClientAPI client, Scene world, AvatarAppearance appearance, PresenceType type)
        {            
            m_scene = world;
            AttachmentsSyncLock = new Object();
            AllowMovement = true;
            IsChildAgent = true;
            IsLoggingIn = false;
            m_sendCoarseLocationsMethod = SendCoarseLocationsDefault;
            Animator = new ScenePresenceAnimator(this);
            Overrides = new MovementAnimationOverrides();
            PresenceType = type;
            DrawDistance = world.DefaultDrawDistance;
            RegionHandle = world.RegionInfo.RegionHandle;
            ControllingClient = client;
            Firstname = ControllingClient.FirstName;
            Lastname = ControllingClient.LastName;
            m_name = String.Format("{0} {1}", Firstname, Lastname);
            m_uuid = client.AgentId;
            LocalId = m_scene.AllocateLocalId();
            LegacySitOffsets = m_scene.LegacySitOffsets;

            UserAccount account = m_scene.UserAccountService.GetUserAccount(m_scene.RegionInfo.ScopeID, m_uuid);
            if (account != null)
                m_userFlags = account.UserFlags;
            else
                m_userFlags = 0;

            if (account != null)
                UserLevel = account.UserLevel;

            IGroupsModule gm = m_scene.RequestModuleInterface<IGroupsModule>();
            if (gm != null)
               Grouptitle = gm.GetGroupTitle(m_uuid);

            m_scriptEngines = m_scene.RequestModuleInterfaces<IScriptModule>();
            
            AbsolutePosition = posLastMove = posLastSignificantMove = CameraPosition =
               m_reprioritizationLastPosition = ControllingClient.StartPos;

            // disable updates workjobs for now
            childUpdatesBusy = true;  
            m_reprioritizationBusy = true;
 
            AdjustKnownSeeds();

            RegisterToEvents();
            SetDirectionVectors();

            Appearance = appearance;

            m_stateMachine = new ScenePresenceStateMachine(this);

            IConfig sconfig = m_scene.Config.Configs["EntityTransfer"];
            if (sconfig != null)
            {
                string lpb = sconfig.GetString("LandingPointBehavior", "LandingPointBehavior_OS");
                if (lpb == "LandingPointBehavior_SL")
                    m_LandingPointBehavior = LandingPointBehavior.SL;
            }

        }

        private void RegionHeartbeatEnd(Scene scene)
        {
            if (IsChildAgent)
                return;

            m_movementAnimationUpdateCounter ++;
            if (m_movementAnimationUpdateCounter >= 2)
            {
                m_movementAnimationUpdateCounter = 0;
                if (Animator != null)
                {
                    // If the parentID == 0 we are not sitting
                    // if !SitGournd then we are not sitting on the ground
                    // Fairly straightforward, now here comes the twist
                    // if ParentUUID is NOT UUID.Zero, we are looking to
                    // be sat on an object that isn't there yet. Should
                    // be treated as if sat.
                    if(ParentID == 0 && !SitGround && ParentUUID == UUID.Zero) // skip it if sitting
                        Animator.UpdateMovementAnimations();
                }
                else
                {
                    m_scene.EventManager.OnRegionHeartbeatEnd -= RegionHeartbeatEnd;
                }
            }
        }

        public void RegisterToEvents()
        {
            ControllingClient.OnCompleteMovementToRegion += CompleteMovement;
            ControllingClient.OnAgentUpdate += HandleAgentUpdate;
            ControllingClient.OnAgentCameraUpdate += HandleAgentCamerasUpdate;
            ControllingClient.OnAgentRequestSit += HandleAgentRequestSit;
            ControllingClient.OnAgentSit += HandleAgentSit;
            ControllingClient.OnSetAlwaysRun += HandleSetAlwaysRun;
            ControllingClient.OnStartAnim += HandleStartAnim;
            ControllingClient.OnStopAnim += HandleStopAnim;
            ControllingClient.OnChangeAnim += avnHandleChangeAnim;
            ControllingClient.OnForceReleaseControls += HandleForceReleaseControls;
            ControllingClient.OnAutoPilotGo += MoveToTarget;
            ControllingClient.OnUpdateThrottles += RaiseUpdateThrottles;

            // ControllingClient.OnChildAgentStatus += new StatusChange(this.ChildStatusChange);
            // ControllingClient.OnStopMovement += new GenericCall2(this.StopMovement);
        }

        private void SetDirectionVectors()
        {
            Dir_Vectors[0] = new Vector3(AgentControlNormalVel,0,0); //FORWARD
            Dir_Vectors[1] = new Vector3(-AgentControlNormalVel,0,0);; //BACK
            Dir_Vectors[2] = new Vector3(0,AgentControlNormalVel,0); //LEFT
            Dir_Vectors[3] = new Vector3(0,-AgentControlNormalVel,0); //RIGHT
            Dir_Vectors[4] = new Vector3(0,0,AgentControlNormalVel); //UP
            Dir_Vectors[5] = new Vector3(0,0,-AgentControlNormalVel); //DOWN
            Dir_Vectors[6] = new Vector3(AgentControlNudgeVel, 0f, 0f); //FORWARD_NUDGE
            Dir_Vectors[7] = new Vector3(-AgentControlNudgeVel, 0f, 0f);  //BACK_NUDGE
            Dir_Vectors[8] = new Vector3(0f, AgentControlNudgeVel, 0f);  //LEFT_NUDGE
            Dir_Vectors[9] = new Vector3(0f, -AgentControlNudgeVel, 0f);  //RIGHT_NUDGE
            Dir_Vectors[10] = new Vector3(0f, 0f, AgentControlNudgeVel); //UP_Nudge
            Dir_Vectors[11] = new Vector3(0f, 0f, -AgentControlNudgeVel); //DOWN_Nudge
        }
        #endregion

        #region Status Methods

        /// <summary>
        /// Turns a child agent into a root agent.
        /// </summary>
        /// <remarks>
        /// Child agents are logged into neighbouring sims largely to observe changes.  Root agents exist when the
        /// avatar is actual in the sim.  They can perform all actions.
        /// This change is made whenever an avatar enters a region, whether by crossing over from a neighbouring sim,
        /// teleporting in or on initial login.
        ///
        /// This method is on the critical path for transferring an avatar from one region to another.  Delay here
        /// delays that crossing.
        /// </remarks>
        
        // constants for physics position search
        const float PhysSearchHeight = 600f;
        const float PhysMinSkipGap = 50f;
        const int PhysNumberCollisions = 30;

        // only in use as part of completemovement
        // other uses need fix
        private bool MakeRootAgent(Vector3 pos, bool isFlying, ref Vector3 lookat)
        {
            int ts = Util.EnvironmentTickCount();

            lock (m_completeMovementLock)
            {
                if (!IsChildAgent)
                    return false;

                m_log.DebugFormat("[MakeRootAgent] enter lock: {0}ms", Util.EnvironmentTickCountSubtract(ts));
                //m_log.DebugFormat("[SCENE]: known regions in {0}: {1}", Scene.RegionInfo.RegionName, KnownChildRegionHandles.Count);

                //            m_log.InfoFormat(
                //                "[SCENE]: Upgrading child to root agent for {0} in {1}",
                //                Name, m_scene.RegionInfo.RegionName);

                if (ParentUUID != UUID.Zero)
                {
                    m_log.DebugFormat("[SCENE PRESENCE]: Sitting avatar back on prim {0}", ParentUUID);
                    SceneObjectPart part = m_scene.GetSceneObjectPart(ParentUUID);
                    if (part == null)
                    {
                        m_log.ErrorFormat("[SCENE PRESENCE]: Can't find prim {0} to sit on", ParentUUID);
                        ParentID = 0;
                        ParentPart = null;
                        PrevSitOffset = Vector3.Zero;
                        HandleForceReleaseControls(ControllingClient, UUID); // needs testing
                        IsLoggingIn = false;
                    }
                    else
                    {
                        part.AddSittingAvatar(this);
                        if (part.SitTargetPosition != Vector3.Zero)
                            part.SitTargetAvatar = UUID;
                        ParentID = part.LocalId;
                        ParentPart = part;
                        m_pos = PrevSitOffset;
                        pos = part.GetWorldPosition();
                    }
                    ParentUUID = UUID.Zero;
                }
                else
                {
                    IsLoggingIn = false;
                }

                IsChildAgent = false;
            }

            m_log.DebugFormat("[MakeRootAgent] out lock: {0}ms", Util.EnvironmentTickCountSubtract(ts));

            // Must reset this here so that a teleport to a region next to an existing region does not keep the flag
            // set and prevent the close of the connection on a subsequent re-teleport.
            // Should not be needed if we are not trying to tell this region to close
            //            DoNotCloseAfterTeleport = false;

            IGroupsModule gm = m_scene.RequestModuleInterface<IGroupsModule>();
            if (gm != null)
                Grouptitle = gm.GetGroupTitle(m_uuid);

            m_log.DebugFormat("[MakeRootAgent] Grouptitle: {0}ms", Util.EnvironmentTickCountSubtract(ts));

            RegionHandle = m_scene.RegionInfo.RegionHandle;

            m_scene.EventManager.TriggerSetRootAgentScene(m_uuid, m_scene);
            m_log.DebugFormat("[MakeRootAgent] TriggerSetRootAgentScene: {0}ms", Util.EnvironmentTickCountSubtract(ts));

            if (ParentID == 0)
            {
                bool positionChanged = false;
                bool success = true;
                if (m_LandingPointBehavior == LandingPointBehavior.OS)
                    success = CheckAndAdjustLandingPoint_OS(ref pos, ref lookat, ref positionChanged);
                else
                    success = CheckAndAdjustLandingPoint_SL(ref pos, ref lookat, ref positionChanged);

                if (!success)
                    m_log.DebugFormat("[SCENE PRESENCE MakeRootAgent]: houston we have a problem.. {0} ({1} got banned)", Name, UUID);

                if (pos.X < 0f || pos.Y < 0f
                          || pos.X >= m_scene.RegionInfo.RegionSizeX
                          || pos.Y >= m_scene.RegionInfo.RegionSizeY)
                {
                    m_log.WarnFormat(
                        "[SCENE PRESENCE]: MakeRootAgent() was given an illegal position of {0} for avatar {1}, {2}. Clamping",
                        pos, Name, UUID);

                    if (pos.X < 0f)
                        pos.X = 0.5f;
                    else if(pos.X >= m_scene.RegionInfo.RegionSizeX)
                        pos.X = m_scene.RegionInfo.RegionSizeX - 0.5f;
                    if (pos.Y < 0f)
                        pos.Y = 0.5f;
                    else if(pos.Y >= m_scene.RegionInfo.RegionSizeY)
                        pos.Y = m_scene.RegionInfo.RegionSizeY - 0.5f;
                }

                float groundHeight = m_scene.GetGroundHeight(pos.X, pos.Y) + .01f;
                float physTestHeight;

                if(PhysSearchHeight < groundHeight + 100f)
                    physTestHeight = groundHeight + 100f;
                else
                    physTestHeight = PhysSearchHeight;

                float localAVHalfHeight = 0.8f;
                if (Appearance != null && Appearance.AvatarHeight > 0)
                    localAVHalfHeight = 0.5f * Appearance.AvatarHeight;

                groundHeight += localAVHalfHeight;
                if (groundHeight > pos.Z)
                    pos.Z = groundHeight;

                bool checkPhysics = !positionChanged &&
                        m_scene.SupportsRayCastFiltered() &&
                        pos.Z < physTestHeight &&
                        ((m_teleportFlags & (TeleportFlags.ViaLogin | TeleportFlags.ViaRegionID)) ==
                            (TeleportFlags.ViaLogin | TeleportFlags.ViaRegionID)
                        || (m_teleportFlags & TeleportFlags.ViaLocation) != 0
                        || (m_teleportFlags & TeleportFlags.ViaHGLogin) != 0);

                if(checkPhysics)
                {
                    // land check was done above
                    RayFilterFlags rayfilter = RayFilterFlags.BackFaceCull;
                        rayfilter |= RayFilterFlags.PrimsNonPhantomAgents;

                    int physcount = PhysNumberCollisions;

                    float dist = physTestHeight - groundHeight + localAVHalfHeight;
                    
                    Vector3 direction = new Vector3(0f, 0f, -1f);
                    Vector3 RayStart = pos;
                    RayStart.Z = physTestHeight;

                    List<ContactResult> physresults =
                            (List<ContactResult>)m_scene.RayCastFiltered(RayStart, direction, dist, physcount, rayfilter);
                    if (physresults != null && physresults.Count > 0)
                    {
                        float dest = physresults[0].Pos.Z;

                        if(physresults.Count > 1)
                        {
                            physresults.Sort(delegate(ContactResult a, ContactResult b)
                            {
                                return a.Depth.CompareTo(b.Depth);
                            });

                            int sel = 0;
                            int count = physresults.Count;
                            float curd = physresults[0].Depth;
                            float nextd = curd + PhysMinSkipGap;
                            float maxDepth = dist - pos.Z;
                            for(int i = 1; i < count; i++)
                            {
                                curd = physresults[i].Depth;
                                if(curd >= nextd)
                                {
                                    sel = i;
                                    if(curd >= maxDepth)
                                        break;
                                }
                                nextd = curd + PhysMinSkipGap;
                            }
                            dest = physresults[sel].Pos.Z;
                        }

                        dest += localAVHalfHeight;
                        if(dest > pos.Z)
                            pos.Z = dest;
                    }
                }

                AbsolutePosition = pos;

//                m_log.DebugFormat(
//                    "Set pos {0}, vel {1} in {1} to {2} from input position of {3} on MakeRootAgent", 
//                    Name, Scene.Name, AbsolutePosition, pos);
//
                if (m_teleportFlags == TeleportFlags.Default)
                {
                    Vector3 vel = Velocity;
                    AddToPhysicalScene(isFlying);
                    if (PhysicsActor != null)
                        PhysicsActor.SetMomentum(vel);
                }
                else
                {
                    AddToPhysicalScene(isFlying);

                    // reset camera to avatar pos
                    CameraPosition = pos;
                }

                if (ForceFly)
                {
                    Flying = true;
                }
                else if (FlyDisabled)
                {
                    Flying = false;
                }
            }
    
 
            m_log.DebugFormat("[MakeRootAgent] position and physical: {0}ms", Util.EnvironmentTickCountSubtract(ts));
            m_scene.SwapRootAgentCount(false);

            // If we don't reset the movement flag here, an avatar that crosses to a neighbouring sim and returns will
            // stall on the border crossing since the existing child agent will still have the last movement
            // recorded, which stops the input from being processed.
            MovementFlag = 0;

            m_scene.EventManager.TriggerOnMakeRootAgent(this);
            m_log.DebugFormat("[MakeRootAgent] TriggerOnMakeRootAgent and done: {0}ms", Util.EnvironmentTickCountSubtract(ts));

            return true;
        }

        private void RestartAttachmentScripts()
        {
            // We need to restart scripts here so that they receive the correct changed events (CHANGED_TELEPORT
            // and CHANGED_REGION) when the attachments have been rezzed in the new region.  This cannot currently
            // be done in AttachmentsModule.CopyAttachments(AgentData ad, IScenePresence sp) itself since we are
            // not transporting the required data.
            //
            // We must take a copy of the attachments list here (rather than locking) to avoid a deadlock where a script in one of
            // the attachments may start processing an event (which locks ScriptInstance.m_Script) that then calls a method here
            // which needs to lock m_attachments.  ResumeScripts() needs to take a ScriptInstance.m_Script lock to try to unset the Suspend status.
            //
            // FIXME: In theory, this deadlock should not arise since scripts should not be processing events until ResumeScripts().
            // But XEngine starts all scripts unsuspended.  Starting them suspended will not currently work because script rezzing
            // is placed in an asynchronous queue in XEngine and so the ResumeScripts() call will almost certainly execute before the
            // script is rezzed.  This means the ResumeScripts() does absolutely nothing when using XEngine.
            List<SceneObjectGroup> attachments = GetAttachments();

            m_log.DebugFormat(
                "[SCENE PRESENCE]: Restarting scripts in {0} attachments for {1} in {2}", attachments.Count, Name, Scene.Name);

            // Resume scripts
            foreach (SceneObjectGroup sog in attachments)
            {
                sog.ScheduleGroupForFullUpdate();
                sog.RootPart.ParentGroup.CreateScriptInstances(0, false, m_scene.DefaultScriptEngine, GetStateSource());
                sog.ResumeScripts();
            }
        }

        private static bool IsRealLogin(TeleportFlags teleportFlags)
        {
            return ((teleportFlags & TeleportFlags.ViaLogin) != 0) && ((teleportFlags & TeleportFlags.ViaHGLogin) == 0);
        }

        /// <summary>
        /// Force viewers to show the avatar's current name.
        /// </summary>
        /// <remarks>
        /// The avatar name that is shown above the avatar in the viewers is sent in ObjectUpdate packets,
        /// and they get the name from the ScenePresence. Unfortunately, viewers have a bug (as of April 2014)
        /// where they ignore changes to the avatar name. However, tey don't ignore changes to the avatar's
        /// Group Title. So the following trick makes viewers update the avatar's name by briefly changing
        /// the group title (to "(Loading)"), and then restoring it.
        /// </remarks>
/*
        public void ForceViewersUpdateName()
        {
            m_log.DebugFormat("[SCENE PRESENCE]: Forcing viewers to update the avatar name for " + Name);

            UseFakeGroupTitle = true;
  

            Util.FireAndForget(o =>
            {
                // Viewers only update the avatar name when idle. Therefore, we must wait long
                // enough for the viewer to show the fake name that we had set above, and only
                // then switch back to the true name. This delay was chosen because it has a high
                // chance of succeeding (we don't want to choose a value that's too low).
                Thread.Sleep(5000);

                UseFakeGroupTitle = false;
                SendAvatarDataToAllClients(false);
            }, null, "Scenepresence.ForceViewersUpdateName");
        }
*/
        public int GetStateSource()
        {
            AgentCircuitData aCircuit = m_scene.AuthenticateHandler.GetAgentCircuitData(UUID);

            if (aCircuit != null && (aCircuit.teleportFlags != (uint)TeleportFlags.Default))
            {
                // This will get your attention
                //m_log.Error("[XXX] Triggering CHANGED_TELEPORT");

                return 5; // StateSource.Teleporting
            }
            return 2; // StateSource.PrimCrossing
        }

        /// <summary>
        /// This turns a root agent into a child agent
        /// </summary>
        /// <remarks>
        /// when an agent departs this region for a neighbor, this gets called.
        ///
        /// It doesn't get called for a teleport.  Reason being, an agent that
        /// teleports out may not end up anywhere near this region
        /// </remarks>
        public void MakeChildAgent(ulong newRegionHandle)
        {
            m_scene.EventManager.OnRegionHeartbeatEnd -= RegionHeartbeatEnd;

            RegionHandle = newRegionHandle;

            m_log.DebugFormat("[SCENE PRESENCE]: Making {0} a child agent in {1} from root region {2}",
                Name, Scene.RegionInfo.RegionName, newRegionHandle);

            // Reset the m_originRegionID as it has dual use as a flag to signal that the UpdateAgent() call orignating
            // from the source simulator has completed on a V2 teleport.
            lock (m_originRegionIDAccessLock)
                m_originRegionID = UUID.Zero;

            // Reset these so that teleporting in and walking out isn't seen
            // as teleporting back
            TeleportFlags = TeleportFlags.Default;

            MovementFlag = 0;

            // It looks like Animator is set to null somewhere, and MakeChild
            // is called after that. Probably in aborted teleports.
            if (Animator == null)
                Animator = new ScenePresenceAnimator(this);
            else
                Animator.ResetAnimations();

           
//            m_log.DebugFormat(
//                 "[SCENE PRESENCE]: Downgrading root agent {0}, {1} to a child agent in {2}",
//                 Name, UUID, m_scene.RegionInfo.RegionName);

            // Don't zero out the velocity since this can cause problems when an avatar is making a region crossing,
            // depending on the exact timing.  This shouldn't matter anyway since child agent positions are not updated.
            //Velocity = new Vector3(0, 0, 0);
            
            IsChildAgent = true;
            m_scene.SwapRootAgentCount(true);
            RemoveFromPhysicalScene();
            ParentID = 0; // Child agents can't be sitting

// we dont have land information for child
            m_previusParcelHide = false;
            m_previusParcelUUID = UUID.Zero;
            m_currentParcelHide = false;
            m_currentParcelUUID = UUID.Zero;
            // FIXME: Set RegionHandle to the region handle of the scene this agent is moving into

            CollisionPlane = Vector4.UnitW;

            m_scene.EventManager.TriggerOnMakeChildAgent(this);
        }

        /// <summary>
        /// Removes physics plugin scene representation of this agent if it exists.
        /// </summary>
        public void RemoveFromPhysicalScene()
        {
            if (PhysicsActor != null)
            {
//                PhysicsActor.OnRequestTerseUpdate -= SendTerseUpdateToAllClients;

                PhysicsActor.OnOutOfBounds -= OutOfBoundsCall;
                PhysicsActor.OnCollisionUpdate -= PhysicsCollisionUpdate;
                PhysicsActor.UnSubscribeEvents();
                m_scene.PhysicsScene.RemoveAvatar(PhysicsActor);
                PhysicsActor = null;
            }
//            else
//            {
//                m_log.ErrorFormat(
//                    "[SCENE PRESENCE]: Attempt to remove physics actor for {0} on {1} but this scene presence has no physics actor",
//                    Name, Scene.RegionInfo.RegionName);
//            }
        }

        /// <summary>
        /// Do not call this directly.  Call Scene.RequestTeleportLocation() instead.
        /// </summary>
        /// <param name="pos"></param>
        public void Teleport(Vector3 pos)
        {
            TeleportWithMomentum(pos, Vector3.Zero);
        }

        public void TeleportWithMomentum(Vector3 pos, Vector3? v)
        {
            if(!CheckLocalTPLandingPoint(ref pos))
                return;

            if (ParentID != (uint)0)
                StandUp();

            bool isFlying = Flying;
            Vector3 vel = Velocity;
            RemoveFromPhysicalScene();
            
            AbsolutePosition = pos;
            AddToPhysicalScene(isFlying);
            if (PhysicsActor != null)
            {
                if (v.HasValue)
                    PhysicsActor.SetMomentum((Vector3)v);
                else
                    PhysicsActor.SetMomentum(vel);
            }

            SendTerseUpdateToAllClients();
        }

        public void avnLocalTeleport(Vector3 newpos, Vector3? newvel, bool rotateToVelXY)
        {
            if(!CheckLocalTPLandingPoint(ref newpos))
                return;

            AbsolutePosition = newpos;

            if (newvel.HasValue)
            {
                if ((Vector3)newvel == Vector3.Zero)
                {
                    if (PhysicsActor != null)
                        PhysicsActor.SetMomentum(Vector3.Zero);
                    m_velocity = Vector3.Zero;
                }
                else
                {
                    if (PhysicsActor != null)
                        PhysicsActor.SetMomentum((Vector3)newvel);
                    m_velocity = (Vector3)newvel;

                    if (rotateToVelXY)
                    {
                        Vector3 lookAt = (Vector3)newvel;
                        lookAt.Z = 0;
                        lookAt.Normalize();
                        ControllingClient.SendLocalTeleport(newpos, lookAt, (uint)TeleportFlags.ViaLocation);
                        return;
                    }
                }
            }
            SendTerseUpdateToAllClients();
        }

        public void StopFlying()
        {
            if (IsInTransit)
                return;

            Vector3 pos = AbsolutePosition; 
            if (Appearance.AvatarHeight != 127.0f)
                pos += new Vector3(0f, 0f, (Appearance.AvatarHeight / 6f));
            else
                pos += new Vector3(0f, 0f, (1.56f / 6f));

            AbsolutePosition = pos;

            // attach a suitable collision plane regardless of the actual situation to force the LLClient to land.
            // Collision plane below the avatar's position a 6th of the avatar's height is suitable.
            // Mind you, that this method doesn't get called if the avatar's velocity magnitude is greater then a
            // certain amount..   because the LLClient wouldn't land in that situation anyway.

            // why are we still testing for this really old height value default???
            if (Appearance.AvatarHeight != 127.0f)
                CollisionPlane = new Vector4(0, 0, 0, pos.Z - Appearance.AvatarHeight / 6f);
            else
                CollisionPlane = new Vector4(0, 0, 0, pos.Z - (1.56f / 6f));

            SendAgentTerseUpdate(this);
        }

        /// <summary>
        /// Applies a roll accumulator to the avatar's angular velocity for the avatar fly roll effect.
        /// </summary>
        /// <param name="amount">Postive or negative roll amount in radians</param>
        private void ApplyFlyingRoll(float amount, bool PressingUp, bool PressingDown)
        {
            
            float rollAmount = Util.Clamp(m_AngularVelocity.Z + amount, -FLY_ROLL_MAX_RADIANS, FLY_ROLL_MAX_RADIANS);
            m_AngularVelocity.Z = rollAmount;

            // APPLY EXTRA consideration for flying up and flying down during this time.
            // if we're turning left
            if (amount > 0)
            {

                // If we're at the max roll and pressing up, we want to swing BACK a bit
                // Automatically adds noise
                if (PressingUp)
                {
                    if (m_AngularVelocity.Z >= FLY_ROLL_MAX_RADIANS - 0.04f)
                        m_AngularVelocity.Z -= 0.9f;
                }
                // If we're at the max roll and pressing down, we want to swing MORE a bit
                if (PressingDown)
                {
                    if (m_AngularVelocity.Z >= FLY_ROLL_MAX_RADIANS && m_AngularVelocity.Z < FLY_ROLL_MAX_RADIANS + 0.6f)
                        m_AngularVelocity.Z += 0.6f;
                }
            }
            else  // we're turning right.
            {
                // If we're at the max roll and pressing up, we want to swing BACK a bit
                // Automatically adds noise
                if (PressingUp)
                {
                    if (m_AngularVelocity.Z <= (-FLY_ROLL_MAX_RADIANS))
                        m_AngularVelocity.Z += 0.6f;
                }
                // If we're at the max roll and pressing down, we want to swing MORE a bit
                if (PressingDown)
                {
                    if (m_AngularVelocity.Z >= -FLY_ROLL_MAX_RADIANS - 0.6f)
                        m_AngularVelocity.Z -= 0.6f;
                }
            }
        }

        /// <summary>
        /// incrementally sets roll amount to zero
        /// </summary>
        /// <param name="amount">Positive roll amount in radians</param>
        /// <returns></returns>
        private float CalculateFlyingRollResetToZero(float amount)
        {
            const float rollMinRadians = 0f;

            if (m_AngularVelocity.Z > 0)
            {
                
                float leftOverToMin = m_AngularVelocity.Z - rollMinRadians;
                if (amount > leftOverToMin)
                    return -leftOverToMin;
                else
                    return -amount;

            }
            else
            {
                
                float leftOverToMin = -m_AngularVelocity.Z - rollMinRadians;
                if (amount > leftOverToMin)
                    return leftOverToMin;
                else
                    return amount;
            }
        }
        


        // neighbouring regions we have enabled a child agent in
        // holds the seed cap for the child agent in that region
        private Dictionary<ulong, string> m_knownChildRegions = new Dictionary<ulong, string>();

        struct spRegionSizeInfo
        {
            public int sizeX;
            public int sizeY;
        }

        private Dictionary<ulong, spRegionSizeInfo> m_knownChildRegionsSizeInfo = new Dictionary<ulong, spRegionSizeInfo>();


        public void AddNeighbourRegionSizeInfo(GridRegion region)
        {
            lock (m_knownChildRegions)
            {
                spRegionSizeInfo sizeInfo = new spRegionSizeInfo();
                sizeInfo.sizeX = region.RegionSizeX;
                sizeInfo.sizeY = region.RegionSizeY;
                ulong regionHandle = region.RegionHandle;

                if (!m_knownChildRegionsSizeInfo.ContainsKey(regionHandle))
                {
                    m_knownChildRegionsSizeInfo.Add(regionHandle, sizeInfo);

                }
                else
                    m_knownChildRegionsSizeInfo[regionHandle] = sizeInfo;
            }
        }

        public void SetNeighbourRegionSizeInfo(List<GridRegion> regionsList)
        {
            lock (m_knownChildRegions)
            {
                m_knownChildRegionsSizeInfo.Clear();
                foreach (GridRegion region in regionsList)
                {
                    spRegionSizeInfo sizeInfo = new spRegionSizeInfo();
                    sizeInfo.sizeX = region.RegionSizeX;
                    sizeInfo.sizeY = region.RegionSizeY;
                    ulong regionHandle = region.RegionHandle;
                    m_knownChildRegionsSizeInfo.Add(regionHandle, sizeInfo);
                }
            }
        }

        public void RemoveNeighbourRegion(ulong regionHandle)
        {
            lock (m_knownChildRegions)
            {
                // Checking ContainsKey is redundant as Remove works either way and returns a bool
                // This is here to allow the Debug output to be conditional on removal
                //if (m_knownChildRegions.ContainsKey(regionHandle))
                //    m_log.DebugFormat(" !!! removing known region {0} in {1}. Count = {2}", regionHandle, Scene.RegionInfo.RegionName, m_knownChildRegions.Count);
                m_knownChildRegions.Remove(regionHandle);
                m_knownChildRegionsSizeInfo.Remove(regionHandle);
            }
        }

        public void DropOldNeighbours(List<ulong> oldRegions)
        {
            foreach (ulong handle in oldRegions)
            {
                RemoveNeighbourRegion(handle);
                Scene.CapsModule.DropChildSeed(UUID, handle);
            }
        }

        public void DropThisRootRegionFromNeighbours()
        {
            ulong handle = m_scene.RegionInfo.RegionHandle;
            RemoveNeighbourRegion(handle);
            Scene.CapsModule.DropChildSeed(UUID, handle);
        }

        public Dictionary<ulong, string> KnownRegions
        {
            get
            {
                lock (m_knownChildRegions)
                    return new Dictionary<ulong, string>(m_knownChildRegions);
            }
            set
            {
                // Replacing the reference is atomic but we still need to lock on
                // the original dictionary object which may be in use elsewhere
                lock (m_knownChildRegions)
                    m_knownChildRegions = value;
            }
        }

        public List<ulong> KnownRegionHandles
        {
            get
            {
                return new List<ulong>(KnownRegions.Keys);
            }
        }

        public int KnownRegionCount
        {
            get
            {
                lock (m_knownChildRegions)
                    return m_knownChildRegions.Count;
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Sets avatar height in the physics plugin
        /// </summary>
        /// <param name="height">New height of avatar</param>
        public void SetHeight(float height)
        {
            if (PhysicsActor != null && !IsChildAgent)
                PhysicsActor.Size = new Vector3(0.45f, 0.6f, height);
        }

        public void SetSize(Vector3 size, float feetoffset)
        {
            if (PhysicsActor != null && !IsChildAgent)
                PhysicsActor.setAvatarSize(size, feetoffset);
        }

        private bool WaitForUpdateAgent(IClientAPI client)
        {
            // Before the source region executes UpdateAgent
            // (which triggers Scene.IncomingUpdateChildAgent(AgentData cAgentData) here in the destination, 
            // m_originRegionID is UUID.Zero; after, it's non-Zero.  The CompleteMovement sequence initiated from the
            // viewer (in turn triggered by the source region sending it a TeleportFinish event) waits until it's non-zero
//            m_updateAgentReceivedAfterTransferEvent.WaitOne(10000);
			int count = 50;
            UUID originID = UUID.Zero;           

            lock (m_originRegionIDAccessLock)
                originID = m_originRegionID;           


            while (originID.Equals(UUID.Zero) && count-- > 0)
            {
                lock (m_originRegionIDAccessLock)
                    originID = m_originRegionID;

                m_log.DebugFormat("[SCENE PRESENCE]: Agent {0} waiting for update in {1}", client.Name, Scene.Name);
                Thread.Sleep(200);
            }

            if (originID.Equals(UUID.Zero))
            {
                // Movement into region will fail
                m_log.WarnFormat("[SCENE PRESENCE]: Update agent {0} never arrived in {1}", client.Name, Scene.Name);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Complete Avatar's movement into the region.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="openChildAgents">
        /// If true, send notification to neighbour regions to expect
        /// a child agent from the client.  These neighbours can be some distance away, depending right now on the
        /// configuration of DefaultDrawDistance in the [Startup] section of config
        /// </param>
        public void CompleteMovement(IClientAPI client, bool openChildAgents)
        {
            int ts = Util.EnvironmentTickCount();

            m_log.InfoFormat(
                "[SCENE PRESENCE]: Completing movement of {0} into region {1} in position {2}",
                client.Name, Scene.Name, AbsolutePosition);
          
            m_inTransit = true;

            try
            {
                // Make sure it's not a login agent. We don't want to wait for updates during login
                if (!isNPC && !IsRealLogin(m_teleportFlags))
                {

                    // Let's wait until UpdateAgent (called by departing region) is done
                    if (!WaitForUpdateAgent(client))
                        // The sending region never sent the UpdateAgent data, we have to refuse
                        return;
                }

                m_log.DebugFormat("[CompleteMovement] WaitForUpdateAgent: {0}ms", Util.EnvironmentTickCountSubtract(ts));

                bool flying = ((m_AgentControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_FLY) != 0);

                Vector3 look = Lookat;
                if ((Math.Abs(look.X) < 0.01) && (Math.Abs(look.Y) < 0.01))
                {
                    look = Velocity;
                    look.Z = 0;
                    look.Normalize();
                    if ((Math.Abs(look.X) < 0.01) && (Math.Abs(look.Y) < 0.01) )
                        look = new Vector3(0.99f, 0.042f, 0);
                }

                if (!MakeRootAgent(AbsolutePosition, flying, ref look))
                {
                    m_log.DebugFormat(
                        "[SCENE PRESENCE]: Aborting CompleteMovement call for {0} in {1} as they are already root",
                        Name, Scene.Name);

                    return;
                }

                m_log.DebugFormat("[CompleteMovement] MakeRootAgent: {0}ms", Util.EnvironmentTickCountSubtract(ts));


// start sending terrain patchs
                if (!isNPC)
                    Scene.SendLayerData(ControllingClient);

                if (!IsChildAgent && !isNPC)
                {
                    InventoryFolderBase cof = m_scene.InventoryService.GetFolderForType(client.AgentId, (FolderType)46);
                    if (cof == null)
                        COF = UUID.Zero;
                    else
                        COF = cof.ID;

                    m_log.DebugFormat("[ScenePresence]: CompleteMovement COF for {0} is {1}", client.AgentId, COF);
                }

                // Tell the client that we're totally ready
                ControllingClient.MoveAgentIntoRegion(m_scene.RegionInfo, AbsolutePosition, look);

                m_log.DebugFormat("[CompleteMovement] MoveAgentIntoRegion: {0}ms", Util.EnvironmentTickCountSubtract(ts));

                if (!string.IsNullOrEmpty(m_callbackURI))
                {
                    // We cannot sleep here since this would hold up the inbound packet processing thread, as
                    // CompleteMovement() is executed synchronously.  However, it might be better to delay the release
                    // here until we know for sure that the agent is active in this region.  Sending AgentMovementComplete
                    // is not enough for Imprudence clients - there appears to be a small delay (<200ms, <500ms) until they regard this
                    // region as the current region, meaning that a close sent before then will fail the teleport.
                    //                System.Threading.Thread.Sleep(2000);

                    m_log.DebugFormat(
                        "[SCENE PRESENCE]: Releasing {0} {1} with callback to {2}",
                        client.Name, client.AgentId, m_callbackURI);

                    UUID originID;

                    lock (m_originRegionIDAccessLock)
                        originID = m_originRegionID;

                    Scene.SimulationService.ReleaseAgent(originID, UUID, m_callbackURI);
                    m_callbackURI = null;
                }
//            else
//            {
//                m_log.DebugFormat(
//                    "[SCENE PRESENCE]: No callback provided on CompleteMovement of {0} {1} to {2}",
//                    client.Name, client.AgentId, m_scene.RegionInfo.RegionName);
//            }

                m_log.DebugFormat("[CompleteMovement] ReleaseAgent: {0}ms", Util.EnvironmentTickCountSubtract(ts));

                m_previusParcelHide = false;
                m_previusParcelUUID = UUID.Zero;
                m_currentParcelHide = false;
                m_currentParcelUUID = UUID.Zero;

                // send initial land overlay and parcel 
                ILandChannel landch = m_scene.LandChannel;
                if (landch != null)
                    landch.sendClientInitialLandInfo(client);

                if (!IsChildAgent)
                {
                    // verify baked textures and cache
                    bool cachedbaked = false;

                    if (isNPC)
                        cachedbaked = true;
                    else
                    {
                        if (m_scene.AvatarFactory != null)
                            cachedbaked = m_scene.AvatarFactory.ValidateBakedTextureCache(this);

                        // not sure we need this
                        if (!cachedbaked)
                        {
                            if (m_scene.AvatarFactory != null)
                                m_scene.AvatarFactory.QueueAppearanceSave(UUID);
                        }
                    }

                    List<ScenePresence> allpresences = m_scene.GetScenePresences();

                    // send avatar object to all presences including us, so they cross it into region
                    // then hide if necessary
                    SendInitialAvatarDataToAllAgents(allpresences);

                    // send this look
                    SendAppearanceToAgent(this);

                    // send this animations

                    UUID[] animIDs = null;
                    int[] animseqs = null;
                    UUID[] animsobjs = null;

                    if (Animator != null)
                        Animator.GetArrays(out animIDs, out animseqs, out animsobjs);

                    bool haveAnims = (animIDs != null && animseqs != null && animsobjs != null);

                    if (haveAnims)
                        SendAnimPackToAgent(this, animIDs, animseqs, animsobjs);

                    // we should be able to receive updates, etc
                    // so release them
                    m_inTransit = false;

                    // send look and animations to others
                    // if not cached we send greys
                    // uncomented if will wait till avatar does baking 
                    //if (cachedbaked)
                    {
                        foreach (ScenePresence p in allpresences)
                        {
                            if (p == this)
                                continue;

                            if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && p.GodLevel < 200)
                                continue;

                            SendAppearanceToAgentNF(p);
                            if (haveAnims)
                                SendAnimPackToAgentNF(p, animIDs, animseqs, animsobjs);
                        }
                    } // greys if

                    m_log.DebugFormat("[CompleteMovement] ValidateAndSendAppearanceAndAgentData: {0}ms", Util.EnvironmentTickCountSubtract(ts));

                    // attachments
                    if (isNPC || IsRealLogin(m_teleportFlags))
                    {
                        if (Scene.AttachmentsModule != null)
                            // Util.FireAndForget(
                            //      o =>
                            //      {

                            if (!isNPC)
                                Scene.AttachmentsModule.RezAttachments(this);
                            else
                                Util.FireAndForget(x =>
                                {
                                    Scene.AttachmentsModule.RezAttachments(this);
                                });

                        // });
                    }
                    else
                    {
                        if (m_attachments.Count > 0)
                        {
                            m_log.DebugFormat(
                                "[SCENE PRESENCE]: Restarting scripts in attachments for {0} in {1}", Name, Scene.Name);

                            foreach (SceneObjectGroup sog in m_attachments)
                            {
                                sog.RootPart.ParentGroup.CreateScriptInstances(0, false, m_scene.DefaultScriptEngine, GetStateSource());
                                sog.ResumeScripts();
                            }

                            foreach (ScenePresence p in allpresences)
                            {
                                if (p == this)
                                {
                                    SendAttachmentsToAgentNF(this);
                                    continue;
                                }

                                if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && p.GodLevel < 200)
                                    continue;

                                SendAttachmentsToAgentNF(p);
                            }
                        }
                    }

                    m_log.DebugFormat("[CompleteMovement] attachments: {0}ms", Util.EnvironmentTickCountSubtract(ts));
                    if (openChildAgents)
                    {
                        // Create child agents in neighbouring regions
                        IEntityTransferModule m_agentTransfer = m_scene.RequestModuleInterface<IEntityTransferModule>();
                        if (m_agentTransfer != null)
                        {
                            m_agentTransfer.EnableChildAgents(this);
                        }
                        // let updates be sent,  with some delay
                        lastChildUpdatesTime = Util.EnvironmentTickCount() + 10000;
                        childUpdatesBusy = false; // allow them
                    }
                }

                m_log.DebugFormat("[CompleteMovement] openChildAgents: {0}ms", Util.EnvironmentTickCountSubtract(ts));

                // send the rest of the world
                if (m_teleportFlags > 0 && !isNPC || m_currentParcelHide)
                    SendInitialDataToMe();

                // priority uses avatar position only
                m_reprioritizationLastPosition = AbsolutePosition;
                m_reprioritizationLastTime = Util.EnvironmentTickCount() + 15000; // delay it
                m_reprioritizationBusy = false;

                m_log.DebugFormat("[CompleteMovement] SendInitialDataToMe: {0}ms", Util.EnvironmentTickCountSubtract(ts));

                if (!IsChildAgent && openChildAgents)
                {
                    IFriendsModule friendsModule = m_scene.RequestModuleInterface<IFriendsModule>();
                    if (friendsModule != null)
                        friendsModule.SendFriendsOnlineIfNeeded(ControllingClient);

                    m_log.DebugFormat("[CompleteMovement] friendsModule: {0}ms", Util.EnvironmentTickCountSubtract(ts));

                }
            }
            finally
            {
                m_inTransit = false;
            }
            // if hide force a check
 //           if (!IsChildAgent && newhide)
 //           {
 //               ParcelLoginCheck(m_currentParcelUUID);
 //               m_currentParcelHide = newhide;
 //           }

            m_scene.EventManager.OnRegionHeartbeatEnd += RegionHeartbeatEnd;

            m_log.DebugFormat("[CompleteMovement] end: {0}ms", Util.EnvironmentTickCountSubtract(ts));
        }

        /// <summary>
        /// Callback for the Camera view block check.  Gets called with the results of the camera view block test
        /// hitYN is true when there's something in the way.
        /// </summary>
        /// <param name="hitYN"></param>
        /// <param name="collisionPoint"></param>
        /// <param name="localid"></param>
        /// <param name="distance"></param>
        /// 

        private void UpdateCameraCollisionPlane(Vector4 plane)
        {
            if (m_lastCameraCollisionPlane != plane)
            {
                m_lastCameraCollisionPlane = plane;
                ControllingClient.SendCameraConstraint(plane);
            }
        }

        public void RayCastCameraCallback(bool hitYN, Vector3 collisionPoint, uint localid, float distance, Vector3 pNormal)
        {
            const float POSITION_TOLERANCE = 0.02f;
            const float ROTATION_TOLERANCE = 0.02f;

            m_doingCamRayCast = false;
            if (hitYN && localid != LocalId)
            {
                SceneObjectGroup group = m_scene.GetGroupByPrim(localid);
                bool IsPrim = group != null;
                if (IsPrim)
                {
                    SceneObjectPart part = group.GetPart(localid);
                    if (part != null && !part.VolumeDetectActive)
                    {
                        CameraConstraintActive = true;
                        pNormal.X = (float) Math.Round(pNormal.X, 2);
                        pNormal.Y = (float) Math.Round(pNormal.Y, 2);
                        pNormal.Z = (float) Math.Round(pNormal.Z, 2);
                        pNormal.Normalize();
                        collisionPoint.X = (float) Math.Round(collisionPoint.X, 1);
                        collisionPoint.Y = (float) Math.Round(collisionPoint.Y, 1);
                        collisionPoint.Z = (float) Math.Round(collisionPoint.Z, 1);

                        Vector4 plane = new Vector4(pNormal.X, pNormal.Y, pNormal.Z,
                                                    Vector3.Dot(collisionPoint, pNormal));
                        UpdateCameraCollisionPlane(plane);
                    }
                }
                else
                {
                    CameraConstraintActive = true;
                    pNormal.X = (float) Math.Round(pNormal.X, 2);
                    pNormal.Y = (float) Math.Round(pNormal.Y, 2);
                    pNormal.Z = (float) Math.Round(pNormal.Z, 2);
                    pNormal.Normalize();
                    collisionPoint.X = (float) Math.Round(collisionPoint.X, 1);
                    collisionPoint.Y = (float) Math.Round(collisionPoint.Y, 1);
                    collisionPoint.Z = (float) Math.Round(collisionPoint.Z, 1);

                    Vector4 plane = new Vector4(pNormal.X, pNormal.Y, pNormal.Z,
                                                Vector3.Dot(collisionPoint, pNormal));
                    UpdateCameraCollisionPlane(plane);
                }
            }
            else if (!m_pos.ApproxEquals(m_lastPosition, POSITION_TOLERANCE) ||
                     !Rotation.ApproxEquals(m_lastRotation, ROTATION_TOLERANCE))
            {
                Vector4 plane = new Vector4(0.9f, 0.0f, 0.361f, -9000f); // not right...
                UpdateCameraCollisionPlane(plane);
                CameraConstraintActive = false;
            }
        }

        /// <summary>
        /// This is the event handler for client movement. If a client is moving, this event is triggering.
        /// </summary>
        public void HandleAgentUpdate(IClientAPI remoteClient, AgentUpdateArgs agentData)
        {
//            m_log.DebugFormat(
//                "[SCENE PRESENCE]: In {0} received agent update from {1}, flags {2}",
//                Scene.Name, remoteClient.Name, (AgentManager.ControlFlags)agentData.ControlFlags);

            if (IsChildAgent)
            {
//                m_log.DebugFormat("DEBUG: HandleAgentUpdate: child agent in {0}", Scene.Name);
                return;
            }

            if (IsInTransit)
                return;

            #region Sanity Checking

            // This is irritating.  Really.
            if (!AbsolutePosition.IsFinite())
            {
                RemoveFromPhysicalScene();
                m_log.Error("[AVATAR]: NonFinite Avatar position detected... Reset Position. Mantis this please. Error #9999902");

                m_pos = m_LastFinitePos;
                if (!m_pos.IsFinite())
                {
                    m_pos.X = 127f;
                    m_pos.Y = 127f;
                    m_pos.Z = 127f;
                    m_log.Error("[AVATAR]: NonFinite Avatar position detected... Reset Position. Mantis this please. Error #9999903");
                }

                AddToPhysicalScene(false);
            }
            else
            {
                m_LastFinitePos = m_pos;
            }

            #endregion Sanity Checking

            #region Inputs

            AgentManager.ControlFlags flags = (AgentManager.ControlFlags)agentData.ControlFlags;

            // The Agent's Draw distance setting
            // When we get to the point of re-computing neighbors everytime this
            // changes, then start using the agent's drawdistance rather than the 
            // region's draw distance.

            DrawDistance = agentData.Far;

            m_mouseLook = (flags & AgentManager.ControlFlags.AGENT_CONTROL_MOUSELOOK) != 0;

            // FIXME: This does not work as intended because the viewer only sends the lbutton down when the button
            // is first pressed, not whilst it is held down.  If this is required in the future then need to look
            // for an AGENT_CONTROL_LBUTTON_UP event and make sure to handle cases where an initial DOWN is not 
            // received (e.g. on holding LMB down on the avatar in a viewer).
            m_leftButtonDown = (flags & AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_DOWN) != 0;

            #endregion Inputs

//            // Make anims work for client side autopilot
//            if ((flags & AgentManager.ControlFlags.AGENT_CONTROL_AT_POS) != 0)
//                m_updateCount = UPDATE_COUNT;
//
//            // Make turning in place work
//            if ((flags & AgentManager.ControlFlags.AGENT_CONTROL_YAW_POS) != 0 ||
//                (flags & AgentManager.ControlFlags.AGENT_CONTROL_YAW_NEG) != 0)
//                m_updateCount = UPDATE_COUNT;


            if ((flags & AgentManager.ControlFlags.AGENT_CONTROL_STAND_UP) != 0)
            {
                StandUp();
            }

            // Raycast from the avatar's head to the camera to see if there's anything blocking the view
            // this exclude checks may not be complete

            if (m_movementUpdateCount % NumMovementsBetweenRayCast == 0 && m_scene.PhysicsScene.SupportsRayCast())
            {
                if (!m_doingCamRayCast && !m_mouseLook && ParentID == 0)
                {
                    Vector3 posAdjusted = AbsolutePosition;
//                    posAdjusted.Z += 0.5f * Appearance.AvatarSize.Z - 0.5f;
                    posAdjusted.Z += 1.0f; // viewer current camera focus point
                    Vector3 tocam = CameraPosition - posAdjusted;
                    tocam.X = (float)Math.Round(tocam.X, 1);
                    tocam.Y = (float)Math.Round(tocam.Y, 1);
                    tocam.Z = (float)Math.Round(tocam.Z, 1);

                    float distTocamlen = tocam.Length();
                    if (distTocamlen > 0.3f)
                    {
                        tocam *= (1.0f / distTocamlen);
                        posAdjusted.X = (float)Math.Round(posAdjusted.X, 1);
                        posAdjusted.Y = (float)Math.Round(posAdjusted.Y, 1);
                        posAdjusted.Z = (float)Math.Round(posAdjusted.Z, 1);

                        m_doingCamRayCast = true;
                        m_scene.PhysicsScene.RaycastWorld(posAdjusted, tocam, distTocamlen + 1.0f, RayCastCameraCallback);
                    }
                }
                else if (CameraConstraintActive && (m_mouseLook || ParentID != 0))
                {
                    Vector4 plane = new Vector4(0.9f, 0.0f, 0.361f, -10000f); // not right...
                    UpdateCameraCollisionPlane(plane);
                    CameraConstraintActive = false;
                }
            }

            uint flagsForScripts = (uint)flags;
            flags = RemoveIgnoredControls(flags, IgnoredControls);

            if ((flags & AgentManager.ControlFlags.AGENT_CONTROL_SIT_ON_GROUND) != 0)
                HandleAgentSitOnGround();

            // In the future, these values might need to go global.
            // Here's where you get them.
            m_AgentControlFlags = flags;
            m_headrotation = agentData.HeadRotation;
            byte oldState = State;
            State = agentData.State;

            // We need to send this back to the client in order to stop the edit beams
            if ((oldState & (uint)AgentState.Editing) != 0 && State == (uint)AgentState.None)
                SendAgentTerseUpdate(this);

            PhysicsActor actor = PhysicsActor;

            // This will be the case if the agent is sitting on the groudn or on an object.
            if (actor == null)
            {
                SendControlsToScripts(flagsForScripts);
                return;
            }

            if (AllowMovement && !SitGround)
            {
//                m_log.DebugFormat("[SCENE PRESENCE]: Initial body rotation {0} for {1}", agentData.BodyRotation, Name);              
                bool update_rotation = false;
                if (agentData.BodyRotation != Rotation)
                {
                    Rotation = agentData.BodyRotation;
                    update_rotation = true;
                }

                bool update_movementflag = false;

                if (agentData.UseClientAgentPosition)
                {
                    MovingToTarget = (agentData.ClientAgentPosition - AbsolutePosition).Length() > 0.2f;
                    MoveToPositionTarget = agentData.ClientAgentPosition;
                }

                int i = 0;
                bool DCFlagKeyPressed = false;
                Vector3 agent_control_v3 = Vector3.Zero;

                bool newFlying = false;

                if (ForceFly)
                    newFlying = true;
                else if (FlyDisabled)
                    newFlying = false;
                else
                    newFlying = ((flags & AgentManager.ControlFlags.AGENT_CONTROL_FLY) != 0);

                if (actor.Flying != newFlying)
                {
                    // Note: ScenePresence.Flying is actually fetched from the physical actor
                    //     so setting PhysActor.Flying here also sets the ScenePresence's value.
                    actor.Flying = newFlying;
                    update_movementflag = true;
                }

                if (ParentID == 0)
                {
                    bool bAllowUpdateMoveToPosition = false;

                    Vector3[] dirVectors;

                    // use camera up angle when in mouselook and not flying or when holding the left mouse button down and not flying
                    // this prevents 'jumping' in inappropriate situations.
//                    if (!Flying && (m_mouseLook || m_leftButtonDown))
//                        dirVectors = GetWalkDirectionVectors();
//                    else
                        dirVectors = Dir_Vectors;

                    // A DIR_CONTROL_FLAG occurs when the user is trying to move in a particular direction.
                    foreach (Dir_ControlFlags DCF in DIR_CONTROL_FLAGS)
                    {
                        if (((uint)flags & (uint)DCF) != 0)
                        {
                            DCFlagKeyPressed = true;

                            try
                            {
                                agent_control_v3 += Dir_Vectors[i];
                                //m_log.DebugFormat("[Motion]: {0}, {1}",i, dirVectors[i]);
                            }
                            catch (IndexOutOfRangeException)
                            {
                                // Why did I get this?
                            }

                            if (((MovementFlag & (uint)DCF) == 0))
                            {   
                                //m_log.DebugFormat("[SCENE PRESENCE]: Updating MovementFlag for {0} with {1}", Name, DCF);
                                MovementFlag |= (uint)DCF;
                                update_movementflag = true;
                            }
                        }
                        else
                        {
                            if ((MovementFlag & (uint)DCF) != 0)
                            {
                                //m_log.DebugFormat("[SCENE PRESENCE]: Updating MovementFlag for {0} with lack of {1}", Name, DCF);
                                MovementFlag &= (uint)~DCF;
                                update_movementflag = true;

                                /*
                                    if ((DCF == Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD_NUDGE || DCF == Dir_ControlFlags.DIR_CONTROL_FLAG_BACKWARD_NUDGE)
                                    && ((MovementFlag & (byte)nudgehack) == nudgehack))
                                    {
                                        m_log.Debug("Removed Hack flag");
                                    }
                                */
                            }
                            else
                            {
                                bAllowUpdateMoveToPosition = true;
                            }
                        }

                        i++;
                    }

                    // Detect AGENT_CONTROL_STOP state changes
                    if (AgentControlStopActive != ((flags & AgentManager.ControlFlags.AGENT_CONTROL_STOP) != 0))
                    {
                        AgentControlStopActive = !AgentControlStopActive;
                        update_movementflag = true;
                    }

                    if (MovingToTarget)
                    {
                        // If the user has pressed a key then we want to cancel any move to target.
                        if (DCFlagKeyPressed)
                        {
                            ResetMoveToTarget();
                            update_movementflag = true;
                        }
                        else if (bAllowUpdateMoveToPosition)
                        {
                            // The UseClientAgentPosition is set if parcel ban is forcing the avatar to move to a
                            // certain position.  It's only check for tolerance on returning to that position is 0.2
                            // rather than 1, at which point it removes its force target.
                            if (HandleMoveToTargetUpdate(agentData.UseClientAgentPosition ? 0.2f : 1f, ref agent_control_v3))
                                update_movementflag = true;
                        }
                    }
                }

                // Cause the avatar to stop flying if it's colliding
                // with something with the down arrow pressed.

                // Only do this if we're flying
                if (Flying && !ForceFly)
                {
                    // Need to stop in mid air if user holds down AGENT_CONTROL_STOP
                    //                    if (AgentControlStopActive)
                    //                    {
                    //                        agent_control_v3 = Vector3.Zero;
                    //                    }
                    //                    else
                    {
                        // Landing detection code

                        // Are the landing controls requirements filled?
                        bool controlland = (((flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0) ||
                                            ((flags & AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG) != 0));

                        //m_log.Debug("[CONTROL]: " +flags);
                        // Applies a satisfying roll effect to the avatar when flying.
                        if ((flags & AgentManager.ControlFlags.AGENT_CONTROL_TURN_LEFT) != 0 && (flags & AgentManager.ControlFlags.AGENT_CONTROL_YAW_POS) != 0)
                        {
                            ApplyFlyingRoll(
                                FLY_ROLL_RADIANS_PER_UPDATE,
                                (flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0,
                                (flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0);
                        }
                        else if ((flags & AgentManager.ControlFlags.AGENT_CONTROL_TURN_RIGHT) != 0 &&
                                 (flags & AgentManager.ControlFlags.AGENT_CONTROL_YAW_NEG) != 0)
                        {
                            ApplyFlyingRoll(
                                -FLY_ROLL_RADIANS_PER_UPDATE,
                                (flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0,
                                (flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0);
                        }
                        else
                        {
                            if (m_AngularVelocity.Z != 0)
                                m_AngularVelocity.Z += CalculateFlyingRollResetToZero(FLY_ROLL_RESET_RADIANS_PER_UPDATE);
                        }
                        
                        /*
                                                if (Flying && IsColliding && controlland)
                                                {
                                                    // nesting this check because LengthSquared() is expensive and we don't 
                                                    // want to do it every step when flying.
                                                    if ((Velocity.LengthSquared() <= LAND_VELOCITYMAG_MAX))
                                                        StopFlying();
                                                }
                         */
                    }
                }
                else if (IsColliding && agent_control_v3.Z < 0f)
                    agent_control_v3.Z = 0;
//                else if(AgentControlStopActive %% Velocity.Z <0.01f)
                    

//                m_log.DebugFormat("[SCENE PRESENCE]: MovementFlag {0} for {1}", MovementFlag, Name);

                // If the agent update does move the avatar, then calculate the force ready for the velocity update,
                // which occurs later in the main scene loop
                // We also need to update if the user rotates their avatar whilst it is slow walking/running (if they
                // held down AGENT_CONTROL_STOP whilst normal walking/running).  However, we do not want to update
                // if the user rotated whilst holding down AGENT_CONTROL_STOP when already still (which locks the 
                // avatar location in place).
                if (update_movementflag 
                    || (update_rotation && DCFlagKeyPressed && (!AgentControlStopActive || MovementFlag != 0)))
                {

                    if (AgentControlStopActive)
                    {
                        //                        if (MovementFlag == 0 && Animator.Falling)
                        if (MovementFlag == 0 && Animator.currentControlState == ScenePresenceAnimator.motionControlStates.falling)
                        {
                            AddNewMovement(agent_control_v3, AgentControlStopSlowVel, true);
                        }
                        else
                            AddNewMovement(agent_control_v3, AgentControlStopSlowVel);
                    }
                    else
                    {
                        AddNewMovement(agent_control_v3);
                    }

                }

                if (update_movementflag && ParentID == 0)
                {
//                    m_log.DebugFormat("[SCENE PRESENCE]: Updating movement animations for {0}", Name);
                    Animator.UpdateMovementAnimations();
                }

                SendControlsToScripts(flagsForScripts);
            }

            // We need to send this back to the client in order to see the edit beams
            if ((State & (uint)AgentState.Editing) != 0)
                SendAgentTerseUpdate(this);

//            m_scene.EventManager.TriggerOnClientMovement(this);
        }


        /// <summary>
        /// This is the event handler for client cameras. If a client is moving, or moving the camera, this event is triggering.
        /// </summary>
        private void HandleAgentCamerasUpdate(IClientAPI remoteClient, AgentUpdateArgs agentData)
        {
            //m_log.DebugFormat(
            //    "[SCENE PRESENCE]: In {0} received agent camera update from {1}, flags {2}",
            //    Scene.RegionInfo.RegionName, remoteClient.Name, (AgentManager.ControlFlags)agentData.ControlFlags);

            if (IsChildAgent)
            {
                //    // m_log.Debug("DEBUG: HandleAgentUpdate: child agent");
                return;
            }

            ++m_movementUpdateCount;
            if (m_movementUpdateCount < 1)
                m_movementUpdateCount = 1;

//            AgentManager.ControlFlags flags = (AgentManager.ControlFlags)agentData.ControlFlags;

            // Camera location in world.  We'll need to raytrace
            // from this location from time to time.
            CameraPosition = agentData.CameraCenter;
            // Use these three vectors to figure out what the agent is looking at
            // Convert it to a Matrix and/or Quaternion
            CameraAtAxis = agentData.CameraAtAxis;
            CameraLeftAxis = agentData.CameraLeftAxis;
            CameraUpAxis = agentData.CameraUpAxis;

            // The Agent's Draw distance setting
            // When we get to the point of re-computing neighbors everytime this
            // changes, then start using the agent's drawdistance rather than the 
            // region's draw distance.
            
            DrawDistance = agentData.Far;

            // Check if Client has camera in 'follow cam' or 'build' mode.
            Vector3 camdif = (Vector3.One * Rotation - Vector3.One * CameraRotation);

            m_followCamAuto = ((CameraUpAxis.Z > 0.959f && CameraUpAxis.Z < 0.98f)
               && (Math.Abs(camdif.X) < 0.4f && Math.Abs(camdif.Y) < 0.4f)) ? true : false;


            //m_log.DebugFormat("[FollowCam]: {0}", m_followCamAuto);
            // Raycast from the avatar's head to the camera to see if there's anything blocking the view
            if ((m_movementUpdateCount % NumMovementsBetweenRayCast) == 0 && m_scene.PhysicsScene.SupportsRayCast())
            {
                if (m_followCamAuto)
                {
                    Vector3 posAdjusted = m_pos + HEAD_ADJUSTMENT;
                    m_scene.PhysicsScene.RaycastWorld(m_pos, Vector3.Normalize(CameraPosition - posAdjusted), Vector3.Distance(CameraPosition, posAdjusted) + 0.3f, RayCastCameraCallback);
                }
            }

            TriggerScenePresenceUpdated();
        }
        
        /// <summary>
        /// Calculate an update to move the presence to the set target.
        /// </summary>
        /// <remarks>
        /// This doesn't actually perform the movement.  Instead, it adds its vector to agent_control_v3.
        /// </remarks>
        /// <param value="agent_control_v3">Cumulative agent movement that this method will update.</param>
        /// <returns>True if movement has been updated in some way.  False otherwise.</returns>
        public bool HandleMoveToTargetUpdate(float tolerance, ref Vector3 agent_control_v3)
        {
//            m_log.DebugFormat("[SCENE PRESENCE]: Called HandleMoveToTargetUpdate() for {0}", Name);

            bool updated = false;
 
            Vector3 LocalVectorToTarget3D = MoveToPositionTarget - AbsolutePosition;

//            m_log.DebugFormat(
//                "[SCENE PRESENCE]: bAllowUpdateMoveToPosition {0}, m_moveToPositionInProgress {1}, m_autopilotMoving {2}",
//                allowUpdate, m_moveToPositionInProgress, m_autopilotMoving);

            float distanceToTarget;
            if(Flying && !LandAtTarget)
            {
                distanceToTarget = LocalVectorToTarget3D.Length();
            }
            else
            {
                Vector3 hdist = LocalVectorToTarget3D;
                hdist.Z = 0;
                distanceToTarget = hdist.Length();
            }

            // m_log.DebugFormat(
            //      "[SCENE PRESENCE]: Abs pos of {0} is {1}, target {2}, distance {3}",
            //           Name, AbsolutePosition, MoveToPositionTarget, distanceToTarget);

            // Check the error term of the current position in relation to the target position
            if (distanceToTarget <= tolerance)
            {
                // We are close enough to the target
                Velocity = Vector3.Zero;
                AbsolutePosition = MoveToPositionTarget;
                if (Flying)
                {
                if (LandAtTarget)
                    Flying = false;

                // A horrible hack to stop the avatar dead in its tracks rather than having them overshoot
                // the target if flying.
                // We really need to be more subtle (slow the avatar as it approaches the target) or at
                // least be able to set collision status once, rather than 5 times to give it enough
                // weighting so that that PhysicsActor thinks it really is colliding.
                for (int i = 0; i < 5; i++)
                    IsColliding = true;
                }
                ResetMoveToTarget();
                return false;
            }
            else
            {
                try
                {
                    // move avatar in 3D at one meter/second towards target, in avatar coordinate frame.
                    // This movement vector gets added to the velocity through AddNewMovement().
                    // Theoretically we might need a more complex PID approach here if other
                    // unknown forces are acting on the avatar and we need to adaptively respond
                    // to such forces, but the following simple approach seems to works fine.

                    LocalVectorToTarget3D = LocalVectorToTarget3D * Quaternion.Inverse(Rotation); // change to avatar coords

                    LocalVectorToTarget3D.Normalize();

                    // update avatar movement flags. the avatar coordinate system is as follows:
                    //
                    //                        +X (forward)
                    //
                    //                        ^
                    //                        |
                    //                        |
                    //                        |
                    //                        |
                    //     (left) +Y <--------o--------> -Y
                    //                       avatar
                    //                        |
                    //                        |
                    //                        |
                    //                        |
                    //                        v
                    //                        -X
                    //

                    // based on the above avatar coordinate system, classify the movement into
                    // one of left/right/back/forward.

                    const uint noMovFlagsMask = (uint)(~(Dir_ControlFlags.DIR_CONTROL_FLAG_BACK |
                        Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD | Dir_ControlFlags.DIR_CONTROL_FLAG_LEFT |
                        Dir_ControlFlags.DIR_CONTROL_FLAG_RIGHT | Dir_ControlFlags.DIR_CONTROL_FLAG_UP |
                        Dir_ControlFlags.DIR_CONTROL_FLAG_DOWN));

                    MovementFlag &= noMovFlagsMask;
                    AgentControlFlags &= noMovFlagsMask;

                    if (LocalVectorToTarget3D.X < 0) //MoveBack
                    {
                        MovementFlag |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_BACK;
                        AgentControlFlags |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_BACK;
                        updated = true;
                    }
                    else if (LocalVectorToTarget3D.X > 0) //Move Forward
                    {
                        MovementFlag |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD;
                        AgentControlFlags |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD;
                        updated = true;
                    }

                    if (LocalVectorToTarget3D.Y > 0) //MoveLeft
                    {
                        MovementFlag |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_LEFT;
                        AgentControlFlags |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_LEFT;
                        updated = true;
                    }
                    else if (LocalVectorToTarget3D.Y < 0) //MoveRight
                    {
                        MovementFlag |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_RIGHT;
                        AgentControlFlags |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_RIGHT;
                        updated = true;
                    }

                    if (LocalVectorToTarget3D.Z > 0) //Up
                    {
                        // Don't set these flags for up or down - doing so will make the avatar crouch or
                        // keep trying to jump even if walking along level ground
                        //MovementFlag += (byte)(uint)Dir_ControlFlags.DIR_CONTROL_FLAG_UP;
                        //AgentControlFlags
                        //AgentControlFlags |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_UP;
                        updated = true;
                    }
                    else if (LocalVectorToTarget3D.Z < 0) //Down
                    {
                        //MovementFlag += (byte)(uint)Dir_ControlFlags.DIR_CONTROL_FLAG_DOWN;
                        //AgentControlFlags |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_DOWN;
                        updated = true;
                    }

//                        m_log.DebugFormat(
//                            "[SCENE PRESENCE]: HandleMoveToTargetUpdate adding {0} to move vector {1} for {2}",
//                            LocalVectorToTarget3D, agent_control_v3, Name);

                    agent_control_v3 += LocalVectorToTarget3D;
                }
                catch (Exception e)
                {
                    //Avoid system crash, can be slower but...
                    m_log.DebugFormat("Crash! {0}", e.ToString());
                }
            }

            return updated;
//                AddNewMovement(agent_control_v3);
        }

        /// <summary>
        /// Move to the given target over time.
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="noFly">
        /// If true, then don't allow the avatar to fly to the target, even if it's up in the air.
        /// This is to allow movement to targets that are known to be on an elevated platform with a continuous path
        /// from start to finish.
        /// </param>
        /// <param name="landAtTarget">
        /// If true and the avatar starts flying during the move then land at the target.
        /// </param>
        public void MoveToTarget(Vector3 pos, bool noFly, bool landAtTarget)
        {
            if (SitGround)
                StandUp();

//            m_log.DebugFormat(
//                "[SCENE PRESENCE]: Avatar {0} received request to move to position {1} in {2}",
//                Name, pos, m_scene.RegionInfo.RegionName);

            // Allow move to another sub-region within a megaregion
            Vector2 regionSize;
            IRegionCombinerModule regionCombinerModule = m_scene.RequestModuleInterface<IRegionCombinerModule>();
            if (regionCombinerModule != null)
                regionSize = regionCombinerModule.GetSizeOfMegaregion(m_scene.RegionInfo.RegionID);
            else
                regionSize = new Vector2(m_scene.RegionInfo.RegionSizeX, m_scene.RegionInfo.RegionSizeY);

            if (pos.X < 0 || pos.X >= regionSize.X
                || pos.Y < 0 || pos.Y >= regionSize.Y
                || pos.Z < 0)
                return;
     
            float terrainHeight;
            Scene targetScene = m_scene;
            // Get terrain height for sub-region in a megaregion if necessary
        	if (regionCombinerModule != null)
            {
                int X = (int)((m_scene.RegionInfo.WorldLocX) + pos.X);
                int Y = (int)((m_scene.RegionInfo.WorldLocY) + pos.Y);
                GridRegion target_region = m_scene.GridService.GetRegionByPosition(m_scene.RegionInfo.ScopeID, X, Y);
                // If X and Y is NaN, target_region will be null
                if (target_region == null)
                    return;
                UUID target_regionID = target_region.RegionID;             
                SceneManager.Instance.TryGetScene(target_region.RegionID, out targetScene);
                terrainHeight = (float)targetScene.Heightmap[(int)(pos.X % regionSize.X), (int)(pos.Y % regionSize.Y)];
            }
            else
                terrainHeight = m_scene.GetGroundHeight(pos.X, pos.Y);
            
            // dont try to land underground
            terrainHeight += Appearance.AvatarHeight * 0.5f + 0.2f;

            if(terrainHeight > pos.Z)
                pos.Z = terrainHeight;

//            m_log.DebugFormat(
//                "[SCENE PRESENCE]: Avatar {0} set move to target {1} (terrain height {2}) in {3}",
//                Name, pos, terrainHeight, m_scene.RegionInfo.RegionName);
                     
            if (noFly)
                Flying = false;
            else if (pos.Z > terrainHeight || Flying)
                Flying = true;

            LandAtTarget = landAtTarget;
            MovingToTarget = true;
            MoveToPositionTarget = pos;

            // Rotate presence around the z-axis to point in same direction as movement.
            // Ignore z component of vector
            Vector3 localVectorToTarget3D = pos - AbsolutePosition;
            Vector3 localVectorToTarget2D = new Vector3((float)(localVectorToTarget3D.X), (float)(localVectorToTarget3D.Y), 0f);

//            m_log.DebugFormat("[SCENE PRESENCE]: Local vector to target is {0}", localVectorToTarget2D);

            // Calculate the yaw.
            Vector3 angle = new Vector3(0, 0, (float)(Math.Atan2(localVectorToTarget2D.Y, localVectorToTarget2D.X)));

//            m_log.DebugFormat("[SCENE PRESENCE]: Angle is {0}", angle);

            Rotation = Quaternion.CreateFromEulers(angle);
//            m_log.DebugFormat("[SCENE PRESENCE]: Body rot for {0} set to {1}", Name, Rotation);
            
            Vector3 control = Vector3.Zero;
            if(HandleMoveToTargetUpdate(1f, ref control))
                AddNewMovement(control);
        }

        /// <summary>
        /// Reset the move to target.
        /// </summary>
        public void ResetMoveToTarget()
        {
//            m_log.DebugFormat("[SCENE PRESENCE]: Resetting move to target for {0}", Name);

            MovingToTarget = false;
//            MoveToPositionTarget = Vector3.Zero;
            m_forceToApply = null; // cancel possible last action

            // We need to reset the control flag as the ScenePresenceAnimator uses this to determine the correct
            // resting animation (e.g. hover or stand).  NPCs don't have a client that will quickly reset this flag.
            // However, the line is here rather than in the NPC module since it also appears necessary to stop a
            // viewer that uses "go here" from juddering on all subsequent avatar movements.
            AgentControlFlags = (uint)AgentManager.ControlFlags.NONE;
        }

        /// <summary>
        /// Perform the logic necessary to stand the avatar up.  This method also executes
        /// the stand animation.
        /// </summary>
        public void StandUp()
        {
//            m_log.DebugFormat("[SCENE PRESENCE]: StandUp() for {0}", Name);

            bool satOnObject = IsSatOnObject;
            SceneObjectPart part = ParentPart;
            SitGround = false;

            if (satOnObject)
            {
                PrevSitOffset = m_pos; // Save sit offset
                UnRegisterSeatControls(part.ParentGroup.UUID);

                TaskInventoryDictionary taskIDict = part.TaskInventory;
                if (taskIDict != null)
                {
                    lock (taskIDict)
                    {
                        foreach (UUID taskID in taskIDict.Keys)
                        {
                            UnRegisterControlEventsToScript(LocalId, taskID);
                            taskIDict[taskID].PermsMask &= ~(
                                2048 | //PERMISSION_CONTROL_CAMERA
                                4); // PERMISSION_TAKE_CONTROLS
                        }
                    }
                }

//                part.ParentGroup.DeleteAvatar(UUID);

                Quaternion standRotation = part.ParentGroup.RootPart.RotationOffset;
                Vector3 sitPartWorldPosition = part.ParentGroup.AbsolutePosition + m_pos * standRotation;
                ControllingClient.SendClearFollowCamProperties(part.ParentUUID);

                ParentID = 0;
                ParentPart = null;

                if (part.SitTargetAvatar == UUID)
                    standRotation = standRotation * part.SitTargetOrientation;
                else
                    standRotation = standRotation * m_bodyRot;

                m_bodyRot = standRotation;

                Quaternion standRotationZ = new Quaternion(0,0,standRotation.Z,standRotation.W);

                float t = standRotationZ.W * standRotationZ.W + standRotationZ.Z * standRotationZ.Z;
                if (t > 0)
                {
                    t = 1.0f / (float)Math.Sqrt(t);
                    standRotationZ.W *= t;
                    standRotationZ.Z *= t;
                }
                else
                {
                    standRotationZ.W = 1.0f;
                    standRotationZ.Z = 0f;
                }

                Vector3 adjustmentForSitPose = new Vector3(0.75f, 0, m_sitAvatarHeight + .3f) * standRotationZ;

                Vector3 standPos = sitPartWorldPosition + adjustmentForSitPose;
                m_pos = standPos;
            }

            // We need to wait until we have calculated proper stand positions before sitting up the physical 
            // avatar to avoid race conditions.
            if (PhysicsActor == null)
                AddToPhysicalScene(false);

            if (satOnObject)
            {
                m_requestedSitTargetID = 0;
                part.RemoveSittingAvatar(this);
                part.ParentGroup.TriggerScriptChangedEvent(Changed.LINK);

                SendAvatarDataToAllAgents();
            }

            // reset to default sitAnimation
            sitAnimation = "SIT";
            
//            Animator.TrySetMovementAnimation("STAND");
            Animator.SetMovementAnimations("STAND");

            TriggerScenePresenceUpdated();
        }

        private SceneObjectPart FindNextAvailableSitTarget(UUID targetID)
        {
            SceneObjectPart targetPart = m_scene.GetSceneObjectPart(targetID);
            if (targetPart == null)
                return null;

            // If the primitive the player clicked on has a sit target and that sit target is not full, that sit target is used.
            // If the primitive the player clicked on has no sit target, and one or more other linked objects have sit targets that are not full, the sit target of the object with the lowest link number will be used.

            // Get our own copy of the part array, and sort into the order we want to test
            SceneObjectPart[] partArray = targetPart.ParentGroup.Parts;
            Array.Sort(partArray, delegate(SceneObjectPart p1, SceneObjectPart p2)
                       {
                           // we want the originally selected part first, then the rest in link order -- so make the selected part link num (-1)
                           int linkNum1 = p1==targetPart ? -1 : p1.LinkNum;
                           int linkNum2 = p2==targetPart ? -1 : p2.LinkNum;
                           return linkNum1 - linkNum2;
                       }
                );

            //look for prims with explicit sit targets that are available
            foreach (SceneObjectPart part in partArray)
            {
                if (part.IsSitTargetSet && part.SitTargetAvatar == UUID.Zero)
                {
                    //switch the target to this prim
                    return part;
                }
            }

            // no explicit sit target found - use original target
            return targetPart;
        }

        private void SendSitResponse(UUID targetID, Vector3 offset, Quaternion sitOrientation)
        {
            Vector3 cameraEyeOffset = Vector3.Zero;
            Vector3 cameraAtOffset = Vector3.Zero;
            bool forceMouselook = false;

            SceneObjectPart part = FindNextAvailableSitTarget(targetID);
            if (part == null)
                return;


            if (PhysicsActor != null)
                m_sitAvatarHeight = PhysicsActor.Size.Z * 0.5f;

            bool canSit = false;

            if (part.IsSitTargetSet && part.SitTargetAvatar == UUID.Zero)
            {
                offset = part.SitTargetPosition;
                sitOrientation = part.SitTargetOrientation;

                canSit = true;
            }
            else
            {
                if (PhysicsSit(part,offset)) // physics engine 
                    return;

                Vector3 pos = part.AbsolutePosition + offset;

                if (Util.GetDistanceTo(AbsolutePosition, pos) <= 10)
                {
                    AbsolutePosition = pos + new Vector3(0.0f, 0.0f, m_sitAvatarHeight);
                    canSit = true;
                }
            }

            if (canSit)
            {

                if (PhysicsActor != null)
                {
                    // We can remove the physicsActor until they stand up.
                    RemoveFromPhysicalScene();
                }

                if (MovingToTarget)
                    ResetMoveToTarget();

                Velocity = Vector3.Zero;

                part.AddSittingAvatar(this);

                cameraAtOffset = part.GetCameraAtOffset();
                cameraEyeOffset = part.GetCameraEyeOffset();
				
                forceMouselook = part.GetForceMouselook();

                if (!part.IsRoot)
                {
                    sitOrientation = part.RotationOffset * sitOrientation;
                    offset = offset * part.RotationOffset;
                    offset += part.OffsetPosition;

                    if (CameraAtAxis == Vector3.Zero && cameraEyeOffset == Vector3.Zero)
                    {
                        CameraAtAxis = part.ParentGroup.RootPart.GetCameraAtOffset();
                        cameraEyeOffset = part.ParentGroup.RootPart.GetCameraEyeOffset();
                    }
                    else
                    {
                        cameraAtOffset = cameraAtOffset * part.RotationOffset;
                        cameraAtOffset += part.OffsetPosition;
                        cameraEyeOffset = cameraEyeOffset * part.RotationOffset;
                        cameraEyeOffset += part.OffsetPosition;
                    }
                }

                ControllingClient.SendSitResponse(
                    part.ParentGroup.UUID, offset, sitOrientation, false, cameraAtOffset, cameraEyeOffset, forceMouselook);

                m_requestedSitTargetUUID = part.UUID;

                HandleAgentSit(ControllingClient, UUID);

                // Moved here to avoid a race with default sit anim
                // The script event needs to be raised after the default sit anim is set.
                part.ParentGroup.TriggerScriptChangedEvent(Changed.LINK);
            }
        }

        public void HandleAgentRequestSit(IClientAPI remoteClient, UUID agentID, UUID targetID, Vector3 offset)
        {
            if (IsChildAgent)
                return;

            if (ParentID != 0)
            {
                if (ParentPart.UUID == targetID)
                    return; // already sitting here, ignore

                StandUp();
            }

            SceneObjectPart part = FindNextAvailableSitTarget(targetID);

            if (part != null)
            {
                m_requestedSitTargetID = part.LocalId;
                m_requestedSitTargetUUID = part.UUID;
            }
            else
            {
                m_log.Warn("Sit requested on unknown object: " + targetID.ToString());
            }

            SendSitResponse(targetID, offset, Quaternion.Identity);
        }

        // returns  false if does not suport so older sit can be tried
        public bool PhysicsSit(SceneObjectPart part, Vector3 offset)
        {
            if (part == null || part.ParentGroup.IsAttachment)
                return true;

            if ( m_scene.PhysicsScene == null)
                return false;

            if (part.PhysActor == null)
            {
                // none physcis shape
                if (part.PhysicsShapeType == (byte)PhysicsShapeType.None)
                    ControllingClient.SendAlertMessage(" There is no suitable surface to sit on, try another spot.");
                else
                { // non physical phantom  TODO
//                    ControllingClient.SendAlertMessage(" There is no suitable surface to sit on, try another spot.");
                    return false;
                }
                return true;
            }

            if (m_scene.PhysicsScene.SitAvatar(part.PhysActor, AbsolutePosition, CameraPosition, offset, new Vector3(0.35f, 0, 0.65f), PhysicsSitResponse) != 0)
            {
                return true;
            }

            return false;
        }


        private bool CanEnterLandPosition(Vector3 testPos)
        {
            ILandObject land = m_scene.LandChannel.GetLandObject(testPos.X, testPos.Y);

            if (land == null || land.LandData.Name == "NO_LAND")
                return true;

            return land.CanBeOnThisLand(UUID,testPos.Z);
        }

        // status
        //          < 0 ignore
        //          0   bad sit spot
        public void PhysicsSitResponse(int status, uint partID, Vector3 offset, Quaternion Orientation)
        {
            if (status < 0)
                return;

            if (status == 0)
            {
                ControllingClient.SendAlertMessage(" There is no suitable surface to sit on, try another spot.");
                return;
            }

            SceneObjectPart part = m_scene.GetSceneObjectPart(partID);
            if (part == null)
                return;

            Vector3 targetPos = part.GetWorldPosition() + offset * part.GetWorldRotation();     
            if(!CanEnterLandPosition(targetPos))
            {
                ControllingClient.SendAlertMessage(" Sit position on restricted land, try another spot");
                return;
            }

            RemoveFromPhysicalScene();

            if (MovingToTarget)
                ResetMoveToTarget();

            Velocity = Vector3.Zero;
            m_AngularVelocity = Vector3.Zero;

            Vector3 cameraAtOffset = part.GetCameraAtOffset();
            Vector3 cameraEyeOffset = part.GetCameraEyeOffset();
            bool forceMouselook = part.GetForceMouselook();

            m_bodyRot = Orientation;

            if (!part.IsRoot)
            {
                Orientation = part.RotationOffset * Orientation;
                offset = offset * part.RotationOffset;
                offset += part.OffsetPosition;

                if (CameraAtAxis == Vector3.Zero && cameraEyeOffset == Vector3.Zero)
                {
                    CameraAtAxis = part.ParentGroup.RootPart.GetCameraAtOffset();
                    cameraEyeOffset = part.ParentGroup.RootPart.GetCameraEyeOffset();
                }
                else
                {
                    cameraAtOffset = cameraAtOffset * part.RotationOffset;
                    cameraAtOffset += part.OffsetPosition;
                    cameraEyeOffset = cameraEyeOffset * part.RotationOffset;
                    cameraEyeOffset += part.OffsetPosition;
                }
            }

            m_pos = offset;

            ControllingClient.SendSitResponse(
                part.ParentGroup.UUID, offset, Orientation, true, cameraAtOffset, cameraEyeOffset, forceMouselook);
          

            m_requestedSitTargetID = 0;
            part.AddSittingAvatar(this);

            ParentPart = part;
            ParentID = part.LocalId;

            SendAvatarDataToAllAgents();

            if (status == 3)
                sitAnimation = "SIT_GROUND";
            else
                sitAnimation = "SIT";

            Animator.SetMovementAnimations("SIT");
            part.ParentGroup.TriggerScriptChangedEvent(Changed.LINK);
        }

        public void HandleAgentSit(IClientAPI remoteClient, UUID agentID)
        {
            if (IsChildAgent)
                return;

             SceneObjectPart part = m_scene.GetSceneObjectPart(m_requestedSitTargetID);

            if (part != null)
            {
                if (part.ParentGroup.IsAttachment)
                {
                    m_log.WarnFormat(
                        "[SCENE PRESENCE]: Avatar {0} tried to sit on part {1} from object {2} in {3} but this is an attachment for avatar id {4}",
                        Name, part.Name, part.ParentGroup.Name, Scene.Name, part.ParentGroup.AttachedAvatar);

                    return;
                }

                if (part.SitTargetAvatar == UUID)
                {
                    Vector3 sitTargetPos = part.SitTargetPosition;
                    Quaternion sitTargetOrient = part.SitTargetOrientation;

//                        m_log.DebugFormat(
//                            "[SCENE PRESENCE]: Sitting {0} at sit target {1}, {2} on {3} {4}",
//                            Name, sitTargetPos, sitTargetOrient, part.Name, part.LocalId);

                    double x, y, z, m;
                    Vector3 sitOffset;
                    Quaternion r = sitTargetOrient;

                    Vector3 newPos;

                    if (LegacySitOffsets)
                    {
                        double m1,m2;

                        m1 = r.X * r.X + r.Y * r.Y;
                        m2 = r.Z * r.Z + r.W * r.W;

                        // Rotate the vector <0, 0, 1>
                        x = 2 * (r.X * r.Z + r.Y * r.W);
                        y = 2 * (-r.X * r.W + r.Y * r.Z);
                        z = m2 - m1;

                        // Set m to be the square of the norm of r.
                        m = m1 + m2;

                        // This constant is emperically determined to be what is used in SL.
                        // See also http://opensimulator.org/mantis/view.php?id=7096
                        double offset = 0.05;

                        // Normally m will be ~ 1, but if someone passed a handcrafted quaternion
                        // to llSitTarget with values so small that squaring them is rounded off
                        // to zero, then m could be zero. The result of this floating point
                        // round off error (causing us to skip this impossible normalization)
                        // is only 5 cm.
                        if (m > 0.000001)
                        {
                            offset /= m;
                        }

                        Vector3 up = new Vector3((float)x, (float)y, (float)z);
                        sitOffset = up * (float)offset;
                        newPos = sitTargetPos - sitOffset + SIT_TARGET_ADJUSTMENT;
                    }
                    else
                    {
                        m = r.X * r.X + r.Y * r.Y + r.Z * r.Z + r.W * r.W;
                            
                        if (Math.Abs(1.0 - m) > 0.000001)
                        {
                            if(m != 0)
                            {
                                m = 1.0 / Math.Sqrt(m);
                                r.X *= (float)m;
                                r.Y *= (float)m;
                                r.Z *= (float)m;
                                r.W *= (float)m;
                            }
                            else
                            {
                                r.X = 0.0f;
                                r.Y = 0.0f;
                                r.Z = 0.0f;
                                r.W = 1.0f;
                                m = 1.0f;
                            }
                        }

                        x = 2 * (r.X * r.Z + r.Y * r.W);
                        y = 2 * (-r.X * r.W + r.Y * r.Z);
                        z = -r.X * r.X - r.Y * r.Y + r.Z * r.Z + r.W * r.W;
                        Vector3 up = new Vector3((float)x, (float)y, (float)z);   
                        sitOffset = up * Appearance.AvatarHeight * 0.02638f;
                        newPos = sitTargetPos + sitOffset + SIT_TARGET_ADJUSTMENT;
                    }

                    Quaternion newRot;

                    if (part.IsRoot)
                    {
                        newRot = sitTargetOrient;
                    }
                    else
                    {
                        newPos = newPos * part.RotationOffset;
                        newRot = part.RotationOffset * sitTargetOrient;
                    }

                    newPos += part.OffsetPosition;

                    m_pos = newPos;
                    Rotation = newRot;

//                    ParentPosition = part.AbsolutePosition;
                }
                else
                {
                    // An viewer expects to specify sit positions as offsets to the root prim, even if a child prim is
                    // being sat upon.
                    m_pos -= part.GroupPosition;

//                    ParentPosition = part.AbsolutePosition;

//                        m_log.DebugFormat(
//                            "[SCENE PRESENCE]: Sitting {0} at position {1} ({2} + {3}) on part {4} {5} without sit target",
//                            Name, part.AbsolutePosition, m_pos, ParentPosition, part.Name, part.LocalId);
                }

                part.AddSittingAvatar(this);
                ParentPart = part;
                ParentID = m_requestedSitTargetID;
                m_AngularVelocity = Vector3.Zero;
                Velocity = Vector3.Zero;
                RemoveFromPhysicalScene();

                m_requestedSitTargetID = 0;

                SendAvatarDataToAllAgents();

                sitAnimation = "SIT";
                if (!String.IsNullOrEmpty(part.SitAnimation))
                {
                    sitAnimation = part.SitAnimation;
                }
//                Animator.TrySetMovementAnimation(sitAnimation);
                Animator.SetMovementAnimations("SIT");
                TriggerScenePresenceUpdated();
            }
        }

        public void HandleAgentSitOnGround()
        {
            if (IsChildAgent)
                return;

//            m_updateCount = 0;  // Kill animation update burst so that the SIT_G.. will stick..
            m_AngularVelocity = Vector3.Zero;
            sitAnimation = "SIT_GROUND_CONSTRAINED";
//            Animator.TrySetMovementAnimation("SIT_GROUND_CONSTRAINED");
//            TriggerScenePresenceUpdated();
            SitGround = true;
            RemoveFromPhysicalScene();

            Animator.SetMovementAnimations("SITGROUND");
            TriggerScenePresenceUpdated();
        }

        /// <summary>
        /// Event handler for the 'Always run' setting on the client
        /// Tells the physics plugin to increase speed of movement.
        /// </summary>
        public void HandleSetAlwaysRun(IClientAPI remoteClient, bool pSetAlwaysRun)
        {
            SetAlwaysRun = pSetAlwaysRun;
        }

        public void HandleStartAnim(IClientAPI remoteClient, UUID animID)
        {
            Animator.AddAnimation(animID, UUID.Zero);
            TriggerScenePresenceUpdated();
        }

        public void HandleStopAnim(IClientAPI remoteClient, UUID animID)
        {
            Animator.RemoveAnimation(animID, false);
            TriggerScenePresenceUpdated();
        }

        public void avnHandleChangeAnim(UUID animID, bool addRemove,bool sendPack)
        {
            Animator.avnChangeAnim(animID, addRemove, sendPack);
        }



        /// <summary>
        /// Rotate the avatar to the given rotation and apply a movement in the given relative vector
        /// </summary>
        /// <param name="vec">The vector in which to move.  This is relative to the rotation argument</param>
        /// <param name="thisAddSpeedModifier">
        /// Optional additional speed modifier for this particular add.  Default is 1</param>
        public void AddNewMovement(Vector3 vec, float thisAddSpeedModifier = 1, bool breaking = false)
        {
            //            m_log.DebugFormat(
            //                "[SCENE PRESENCE]: Adding new movement {0} with rotation {1}, thisAddSpeedModifier {2} for {3}", 
            //                vec, Rotation, thisAddSpeedModifier, Name);

            // rotate from avatar coord space to world
            // for now all controls assume this is only a rotation around Z
            // if not all checks below need to be done before this rotation
            Vector3 direc = vec * Rotation;
            direc.Normalize();

            // mouse look situation ?
            if ((vec.Z == 0f) && !Flying)
                direc.Z = 0f; // Prevent camera WASD up.

            // odd rescalings
            direc *= 0.03f * 128f * SpeedModifier * thisAddSpeedModifier;

            //            m_log.DebugFormat("[SCENE PRESENCE]: Force to apply before modification was {0} for {1}", direc, Name);

            if (Animator.currentControlState == ScenePresenceAnimator.motionControlStates.falling
                    && (PhysicsActor == null || !PhysicsActor.PIDHoverActive))
            {
                if (breaking)
                    direc.Z = -9999f; //hack to tell physics to stop on Z
                else
                    direc = Vector3.Zero;
            }
            else if (Flying)
            {
                if (IsColliding && direc.Z < 0)
                    // landing situation, prevent avatar moving or it may fail to land
                    // animator will handle this condition and do the land
                    direc = Vector3.Zero;
                else
                    direc *= 4.0f;
            }
            else if (IsColliding)
            {
                if (direc.Z > 2.0f) // reinforce jumps
                {
                    direc.Z *= 2.6f;
                }
                else if (direc.Z < 0) // on a surface moving down (pg down) only changes animation
                    direc.Z = 0;
            }

            //            m_log.DebugFormat("[SCENE PRESENCE]: Setting force to apply to {0} for {1}", direc, Name);

            m_forceToApply = direc;
            Animator.UpdateMovementAnimations();
        }

        #endregion

        #region Overridden Methods

       const float ROTATION_TOLERANCE = 0.01f;
       const float VELOCITY_TOLERANCE = 0.1f;
       const float LOWVELOCITYSQ = 0.1f;
       const float POSITION_LARGETOLERANCE = 5f;
       const float POSITION_SMALLTOLERANCE = 0.05f;

        public override void Update()
        {
            if(IsChildAgent || IsDeleted)
                return;

            CheckForBorderCrossing();

            if (IsInTransit || IsLoggingIn)
                return;

            if(MovingToTarget)
            {
                Vector3 control = Vector3.Zero;
                if(HandleMoveToTargetUpdate(1f, ref control))
                    AddNewMovement(control);
            }

            if (Appearance.AvatarSize != m_lastSize)
                SendAvatarDataToAllAgents();

            // Send terse position update if not sitting and position, velocity, or rotation
            //      has changed significantly from last sent update
            if (!IsSatOnObject && (
                        !Rotation.ApproxEquals(m_lastRotation, ROTATION_TOLERANCE)
                        || !Velocity.ApproxEquals(m_lastVelocity, VELOCITY_TOLERANCE)
                        || !m_pos.ApproxEquals(m_lastPosition, POSITION_LARGETOLERANCE)
                        // if velocity is zero and it wasn't zero last time, send the update
                        || (Velocity == Vector3.Zero && m_lastVelocity != Vector3.Zero)
                        // if position has moved just a little and velocity is very low, send  the update
                        || (!m_pos.ApproxEquals(m_lastPosition, POSITION_SMALLTOLERANCE) && Velocity.LengthSquared() < LOWVELOCITYSQ )
                ) )
            {
/*
            if (!IsSatOnObject)
            {
                // this does need to be more complex later
                Vector3 vel = Velocity;
                Vector3 dpos = m_pos - m_lastPosition;
                if(     Math.Abs(vel.X - m_lastVelocity.X) > VELOCITY_TOLERANCE ||
                        Math.Abs(vel.Y - m_lastVelocity.Y) > VELOCITY_TOLERANCE ||
                        Math.Abs(vel.Z - m_lastVelocity.Z) > VELOCITY_TOLERANCE ||

                        Math.Abs(m_bodyRot.X - m_lastRotation.X) > ROTATION_TOLERANCE ||
                        Math.Abs(m_bodyRot.Y - m_lastRotation.Y) > ROTATION_TOLERANCE ||
                        Math.Abs(m_bodyRot.Z - m_lastRotation.Z) > ROTATION_TOLERANCE ||

                        Math.Abs(dpos.X) > POSITION_LARGETOLERANCE ||
                        Math.Abs(dpos.Y) > POSITION_LARGETOLERANCE ||
                        Math.Abs(dpos.Z) > POSITION_LARGETOLERANCE ||
 
                        (  (Math.Abs(dpos.X) > POSITION_SMALLTOLERANCE ||
                            Math.Abs(dpos.Y) > POSITION_SMALLTOLERANCE ||
                            Math.Abs(dpos.Z) > POSITION_SMALLTOLERANCE)
                            && vel.LengthSquared() < LOWVELOCITYSQ 
                        ))
                {
*/
                    SendTerseUpdateToAllClients();
//                }
            }
            CheckForSignificantMovement();
        }

        #endregion

        #region Update Client(s)

        public void SendUpdateToAgent(ScenePresence p)
        {
            IClientAPI remoteClient = p.ControllingClient;

            if (remoteClient.IsActive)
            {
                //m_log.DebugFormat("[SCENE PRESENCE]: " + Name + " sending TerseUpdate to " + remoteClient.Name + " : Pos={0} Rot={1} Vel={2}", m_pos, Rotation, m_velocity);
                remoteClient.SendEntityUpdate(this, PrimUpdateFlags.FullUpdate);
                m_scene.StatsReporter.AddAgentUpdates(1);
            }
        }

        public void SendFullUpdateToClient(IClientAPI remoteClient)
        {
            if (remoteClient.IsActive)
            {
                //m_log.DebugFormat("[SCENE PRESENCE]: " + Name + " sending TerseUpdate to " + remoteClient.Name + " : Pos={0} Rot={1} Vel={2}", m_pos, Rotation, m_velocity);
                remoteClient.SendEntityUpdate(this, PrimUpdateFlags.FullUpdate);
                m_scene.StatsReporter.AddAgentUpdates(1);
            }
        }

        // this is diferente from SendTerseUpdateToClient
        // this sends bypassing entities updates 
        public void SendAgentTerseUpdate(ISceneEntity p)
        {
            ControllingClient.SendAgentTerseUpdate(p);
        }

        /// <summary>
        /// Sends a location update to the client connected to this scenePresence
        /// via entity updates
        /// </summary>
        /// <param name="remoteClient"></param>
        public void SendTerseUpdateToClient(IClientAPI remoteClient)
        {
            // If the client is inactive, it's getting its updates from another
            // server.
            if (remoteClient.IsActive)
            {
                //m_log.DebugFormat("[SCENE PRESENCE]: " + Name + " sending TerseUpdate to " + remoteClient.Name + " : Pos={0} Rot={1} Vel={2}", m_pos, Rotation, m_velocity);
                remoteClient.SendEntityUpdate(
                    this,
                    PrimUpdateFlags.Position | PrimUpdateFlags.Rotation | PrimUpdateFlags.Velocity
                    | PrimUpdateFlags.Acceleration | PrimUpdateFlags.AngularVelocity);

                m_scene.StatsReporter.AddAgentUpdates(1);
            }
        }

        public void SendTerseUpdateToAgent(ScenePresence p)
        {
            IClientAPI remoteClient = p.ControllingClient;

            if (!remoteClient.IsActive)
                return;

            if (ParcelHideThisAvatar && p.currentParcelUUID != currentParcelUUID && p.GodLevel < 200)
                return;

            //m_log.DebugFormat("[SCENE PRESENCE]: " + Name + " sending TerseUpdate to " + remoteClient.Name + " : Pos={0} Rot={1} Vel={2}", m_pos, Rotation, m_velocity);
            remoteClient.SendEntityUpdate(
                this,
                PrimUpdateFlags.Position | PrimUpdateFlags.Rotation | PrimUpdateFlags.Velocity
                | PrimUpdateFlags.Acceleration | PrimUpdateFlags.AngularVelocity);

            m_scene.StatsReporter.AddAgentUpdates(1);
        }

        public void SendTerseUpdateToAgentNF(ScenePresence p)
        {
            IClientAPI remoteClient = p.ControllingClient;
            if (remoteClient.IsActive)
            {
                //m_log.DebugFormat("[SCENE PRESENCE]: " + Name + " sending TerseUpdate to " + remoteClient.Name + " : Pos={0} Rot={1} Vel={2}", m_pos, Rotation, m_velocity);
                remoteClient.SendEntityUpdate(this,
                    PrimUpdateFlags.Position | PrimUpdateFlags.Rotation | PrimUpdateFlags.Velocity
                    | PrimUpdateFlags.Acceleration | PrimUpdateFlags.AngularVelocity);
                m_scene.StatsReporter.AddAgentUpdates(1);
            }
        }

        /// <summary>
        /// Send a location/velocity/accelleration update to all agents in scene
        /// </summary>
        public void SendTerseUpdateToAllClients()
        {
            m_scene.ForEachScenePresence(SendTerseUpdateToAgent);
            // Update the "last" values
            m_lastPosition = m_pos;
            m_lastRotation = m_bodyRot;
            m_lastVelocity = Velocity;
            TriggerScenePresenceUpdated();
        }

        public void SendCoarseLocations(List<Vector3> coarseLocations, List<UUID> avatarUUIDs)
        {
            SendCoarseLocationsMethod d = m_sendCoarseLocationsMethod;
            if (d != null)
            {
                d.Invoke(m_scene.RegionInfo.originRegionID, this, coarseLocations, avatarUUIDs);
            }
        }

        public void SetSendCoarseLocationMethod(SendCoarseLocationsMethod d)
        {
            if (d != null)
                m_sendCoarseLocationsMethod = d;
        }

        public void SendCoarseLocationsDefault(UUID sceneId, ScenePresence p, List<Vector3> coarseLocations, List<UUID> avatarUUIDs)
        {
            ControllingClient.SendCoarseLocationUpdate(avatarUUIDs, coarseLocations);
        }

        public void SendInitialDataToMe()
        {
            // Send all scene object to the new client
            Util.FireAndForget(delegate
            {
                // we created a new ScenePresence (a new child agent) in a fresh region.
                // Request info about all the (root) agents in this region
                // Note: This won't send data *to* other clients in that region (children don't send)
                if (m_teleportFlags <= 0)
                {
                    Scene.SendLayerData(ControllingClient);

                    ILandChannel landch = m_scene.LandChannel;
                    if (landch != null)
                    {
                        landch.sendClientInitialLandInfo(ControllingClient);
                    }
                }

                SendOtherAgentsAvatarFullToMe();
                EntityBase[] entities = Scene.Entities.GetEntities();
                foreach (EntityBase e in entities)
                {
                    if (e != null && e is SceneObjectGroup && !((SceneObjectGroup)e).IsAttachment)
                        ((SceneObjectGroup)e).SendFullUpdateToClient(ControllingClient);
                }
            });
        }

        /// <summary>
        /// Send avatar full data appearance and animations for all other root agents to this agent, this agent
        /// can be either a child or root
        /// </summary>
        public void SendOtherAgentsAvatarFullToMe()
        {
            int count = 0;
            m_scene.ForEachRootScenePresence(delegate(ScenePresence p)
            {
                // only send information about other root agents
                if (p.UUID == UUID)
                    return;

                // get the avatar, then a kill if can't see it
                p.SendInitialAvatarDataToAgent(this);

                if (p.ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && GodLevel < 200)
                    return;

                p.SendAppearanceToAgentNF(this);
                p.SendAnimPackToAgentNF(this);
                p.SendAttachmentsToAgentNF(this);               
                count++;
            });

            m_scene.StatsReporter.AddAgentUpdates(count);
        }

        /// <summary>
        /// Send this agent's avatar data to all other root and child agents in the scene
        /// This agent must be root. This avatar will receive its own update. 
        /// </summary>
        public void SendAvatarDataToAllAgents()
        {
            //m_log.DebugFormat("[SCENE PRESENCE] SendAvatarDataToAllAgents: {0} ({1})", Name, UUID);
            // only send update from root agents to other clients; children are only "listening posts"
            if (IsChildAgent)
            {
                m_log.WarnFormat(
                    "[SCENE PRESENCE]: Attempt to send avatar data from a child agent for {0} in {1}",
                    Name, Scene.RegionInfo.RegionName);
                return;
            }

            m_lastSize = Appearance.AvatarSize;
            int count = 0;

            m_scene.ForEachScenePresence(delegate(ScenePresence scenePresence)
            {
                SendAvatarDataToAgent(scenePresence);
                count++;
            });

            m_scene.StatsReporter.AddAgentUpdates(count);
        }
        // sends avatar object to all clients so they cross it into region
        // then sends kills to hide
        public void SendInitialAvatarDataToAllAgents(List<ScenePresence> presences)
        {
            m_lastSize = Appearance.AvatarSize;
            int count = 0;
            foreach (ScenePresence p in presences)
            {
                p.ControllingClient.SendAvatarDataImmediate(this);
                if (p != this && ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && p.GodLevel < 200)
                    // either just kill the object
                    // p.ControllingClient.SendKillObject(new List<uint> {LocalId});
                    // or also attachments viewer may still know about
                    SendKillTo(p);
                count++;
            }
            m_scene.StatsReporter.AddAgentUpdates(count);
        }

        public void SendInitialAvatarDataToAgent(ScenePresence p)
        {
            p.ControllingClient.SendAvatarDataImmediate(this);
            if (p != this && ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && p.GodLevel < 200)
                    // either just kill the object
                    // p.ControllingClient.SendKillObject(new List<uint> {LocalId});
                    // or also attachments viewer may still know about
                SendKillTo(p);
        }

        /// <summary>
        /// Send avatar data to an agent.
        /// </summary>
        /// <param name="avatar"></param>
        public void SendAvatarDataToAgent(ScenePresence avatar)
        {
            //m_log.DebugFormat("[SCENE PRESENCE] SendAvatarDataToAgent from {0} ({1}) to {2} ({3})", Name, UUID, avatar.Name, avatar.UUID);
            if (ParcelHideThisAvatar && currentParcelUUID != avatar.currentParcelUUID && avatar.GodLevel < 200)
                return;
            avatar.ControllingClient.SendAvatarDataImmediate(this);
        }

        public void SendAvatarDataToAgentNF(ScenePresence avatar)
        {
             avatar.ControllingClient.SendAvatarDataImmediate(this);
        }

        /// <summary>
        /// Send this agent's appearance to all other root and child agents in the scene
        /// This agent must be root.
        /// </summary>
        public void SendAppearanceToAllOtherAgents()
        {
            //            m_log.DebugFormat("[SCENE PRESENCE] SendAppearanceToAllOtherAgents: {0} {1}", Name, UUID);

            // only send update from root agents to other clients; children are only "listening posts"
            if (IsChildAgent)
            {
                m_log.WarnFormat(
                    "[SCENE PRESENCE]: Attempt to send avatar data from a child agent for {0} in {1}",
                    Name, Scene.RegionInfo.RegionName);

                return;
            }

            int count = 0;
            m_scene.ForEachScenePresence(delegate(ScenePresence scenePresence)
            {
                // only send information to other root agents
                if (scenePresence.UUID == UUID)
                    return;

                SendAppearanceToAgent(scenePresence);
                count++;
            });
            m_scene.StatsReporter.AddAgentUpdates(count);
        }

        public void SendAppearanceToAgent(ScenePresence avatar)
        {
            //            m_log.DebugFormat(
            //                "[SCENE PRESENCE]: Sending appearance data from {0} {1} to {2} {3}", Name, m_uuid, avatar.Name, avatar.UUID);
            if (ParcelHideThisAvatar && currentParcelUUID != avatar.currentParcelUUID && avatar.GodLevel < 200)
                return;
            SendAppearanceToAgentNF(avatar);
        }

        public void SendAppearanceToAgentNF(ScenePresence avatar)
        {
            avatar.ControllingClient.SendAppearance(
                UUID, Appearance.VisualParams, Appearance.Texture.GetBytes());
        }

        public void SendAnimPackToAgent(ScenePresence p)
        {
            if (IsChildAgent || Animator == null)
                return;

            if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && p.GodLevel < 200)
                return;

            Animator.SendAnimPackToClient(p.ControllingClient);
        }

        public void SendAnimPackToAgent(ScenePresence p, UUID[] animations, int[] seqs, UUID[] objectIDs)
        {
            if (IsChildAgent)
                return;

            if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && p.GodLevel < 200)
                return;

            p.ControllingClient.SendAnimations(animations, seqs, ControllingClient.AgentId, objectIDs);
        }

        public void SendAnimPackToAgentNF(ScenePresence p)
        {
            if (IsChildAgent || Animator == null)
                return;
            Animator.SendAnimPackToClient(p.ControllingClient);
        }

        public void SendAnimPackToAgentNF(ScenePresence p, UUID[] animations, int[] seqs, UUID[] objectIDs)
        {
            p.ControllingClient.SendAnimations(animations, seqs, ControllingClient.AgentId, objectIDs);
        }

        public void SendAnimPack(UUID[] animations, int[] seqs, UUID[] objectIDs)
        {
            if (IsChildAgent)
                return;

            m_scene.ForEachScenePresence(delegate(ScenePresence p)
            {
                if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && p.GodLevel < 200)
                    return;
                p.ControllingClient.SendAnimations(animations, seqs, ControllingClient.AgentId, objectIDs);
            });
        }

        #endregion

        #region Significant Movement Method

        private void checkRePrioritization()
        {
            if(IsDeleted || !ControllingClient.IsActive)
                return;

            if(m_reprioritizationBusy)
                return;

            int tdiff =  Util.EnvironmentTickCountSubtract(m_reprioritizationLastTime);
            if(tdiff < Scene.ReprioritizationInterval)
                return;
            // priority uses avatar position
            Vector3 pos = AbsolutePosition;
            Vector3 diff = pos - m_reprioritizationLastPosition;
            float limit;
            if(IsChildAgent)
                limit = (float)Scene.ChildReprioritizationDistance;
            else
                limit = (float)Scene.RootReprioritizationDistance;

            limit *= limit;
            if (diff.LengthSquared() < limit)
                return;

            m_reprioritizationBusy = true;
            m_reprioritizationLastPosition = pos;

            Util.FireAndForget(
                o =>
                {
                    ControllingClient.ReprioritizeUpdates(); 
                    m_reprioritizationLastTime = Util.EnvironmentTickCount();
                    m_reprioritizationBusy = false;
                }, null, "ScenePresence.Reprioritization");
        }
        /// <summary>
        /// This checks for a significant movement and sends a coarselocationchange update
        /// </summary>
        protected void CheckForSignificantMovement()
        {
            Vector3 pos = AbsolutePosition;

            Vector3 diff = pos - posLastMove;
            if (diff.LengthSquared() > MOVEMENT)
            {
                posLastMove = pos;
                m_scene.EventManager.TriggerOnClientMovement(this);
            }
            
            diff = pos - posLastSignificantMove;
            if (diff.LengthSquared() > SIGNIFICANT_MOVEMENT)
            {
                posLastSignificantMove = pos;
                m_scene.EventManager.TriggerSignificantClientMovement(this);
            }

            // updates priority recalc
            checkRePrioritization();

            if(childUpdatesBusy)
                return;
            
            //possible KnownRegionHandles always contains current region and this check is not needed
            int minhandles = 0;
            if(KnownRegionHandles.Contains(RegionHandle))
                    minhandles++;

            if(KnownRegionHandles.Count > minhandles)            
            {              
                int tdiff = Util.EnvironmentTickCountSubtract(lastChildUpdatesTime);
                if(tdiff > CHILDUPDATES_TIME)
                {
                    diff = pos - m_lastChildAgentUpdatePosition; 
                    if (diff.LengthSquared() > CHILDUPDATES_MOVEMENT)
                    {
                        childUpdatesBusy = true;
                        m_lastChildAgentUpdatePosition = pos;
//                        m_lastChildAgentUpdateCamPosition = CameraPosition;

                        AgentPosition agentpos = new AgentPosition();
                        agentpos.AgentID = new UUID(UUID.Guid);
                        agentpos.SessionID = ControllingClient.SessionId;
                        agentpos.Size = Appearance.AvatarSize;
                        agentpos.Center = CameraPosition;
                        agentpos.Far = DrawDistance;
                        agentpos.Position = AbsolutePosition;
                        agentpos.Velocity = Velocity;
                        agentpos.RegionHandle = RegionHandle;
                        agentpos.Throttles = ControllingClient.GetThrottlesPacked(1);

                        // Let's get this out of the update loop
                        Util.FireAndForget(
                            o =>
                            {
                                m_scene.SendOutChildAgentUpdates(agentpos, this); 
                                lastChildUpdatesTime = Util.EnvironmentTickCount();
                                childUpdatesBusy = false;
                            }, null, "ScenePresence.SendOutChildAgentUpdates");
                    }
                }
            }
        }

        #endregion

        #region Border Crossing Methods

        /// <summary>
        /// Starts the process of moving an avatar into another region if they are crossing the border.
        /// </summary>
        /// <remarks>
        /// Also removes the avatar from the physical scene if transit has started.
        /// </remarks>
        protected void CheckForBorderCrossing()
        {
            // Check that we we are not a child
            if (IsChildAgent || IsInTransit)
                return;

            // If we don't have a PhysActor, we can't cross anyway
            // Also don't do this while sat, sitting avatars cross with the
            // object they sit on. ParentUUID denoted a pending sit, don't
            // interfere with it.
            if (ParentID != 0 || PhysicsActor == null || ParentUUID != UUID.Zero)
                return;

            Vector3 pos2 = AbsolutePosition;
            Vector3 vel = Velocity;

            float timeStep = 0.1f;
            pos2.X += vel.X * timeStep;
            pos2.Y += vel.Y * timeStep;
            pos2.Z += vel.Z * timeStep;

//                    m_log.DebugFormat(
//                        "[SCENE PRESENCE]: Testing border check for projected position {0} of {1} in {2}", 
//                        pos2, Name, Scene.Name);

            if (Scene.PositionIsInCurrentRegion(pos2))
                return;

            if (!CrossToNewRegion() && m_requestedSitTargetUUID == UUID.Zero)
            {
                // we don't have entity transfer module
                Vector3 pos = AbsolutePosition;
                float px = pos.X;
                if (px < 0)
                    pos.X += Velocity.X * 2;
                else if (px > m_scene.RegionInfo.RegionSizeX)
                    pos.X -= Velocity.X * 2;

                float py = pos.Y;
                if (py < 0)
                    pos.Y += Velocity.Y * 2;
                else if (py > m_scene.RegionInfo.RegionSizeY)
                    pos.Y -= Velocity.Y * 2;

                Velocity = Vector3.Zero;
                AbsolutePosition = pos;
            }
        }

        public void CrossToNewRegionFail()
        {
            if (m_requestedSitTargetUUID == UUID.Zero)
            {
                bool isFlying = Flying;
                RemoveFromPhysicalScene();

                Vector3 pos = AbsolutePosition;
                float px = pos.X;
                if (px < 0)
                    pos.X += Velocity.X * 2;
                else if (px > m_scene.RegionInfo.RegionSizeX)
                    pos.X -= Velocity.X * 2;

                float py = pos.Y;
                if (py < 0)
                    pos.Y += Velocity.Y * 2;
                else if (py > m_scene.RegionInfo.RegionSizeY)
                    pos.Y -= Velocity.Y * 2;

                Velocity = Vector3.Zero;
                AbsolutePosition = pos;

                AddToPhysicalScene(isFlying);
            }
        }

        /// <summary>
        /// Moves the agent outside the region bounds
        /// Tells neighbor region that we're crossing to it
        /// If the neighbor accepts, remove the agent's viewable avatar from this scene
        /// set them to a child agent.
        /// </summary>
        protected bool CrossToNewRegion()
        {
            bool result = false;
//            parcelRegionCross(false);
            try
            {
                result = m_scene.CrossAgentToNewRegion(this, Flying);
            }
            catch
            {
//                result = m_scene.CrossAgentToNewRegion(this, false);
                return false;
            }
 //           if(!result)
 //               parcelRegionCross(true);

            return result;

        }

        /* useless. Either use MakeChild or delete the presence
                public void Reset()
                {
        //            m_log.DebugFormat("[SCENE PRESENCE]: Resetting {0} in {1}", Name, Scene.RegionInfo.RegionName);

                    // Put the child agent back at the center
                    AbsolutePosition 
                        = new Vector3(((float)m_scene.RegionInfo.RegionSizeX * 0.5f), ((float)m_scene.RegionInfo.RegionSizeY * 0.5f), 70);

                    Animator.ResetAnimations();
                }
        */
        /// <summary>
        /// Computes which child agents to close when the scene presence moves to another region.
        /// Removes those regions from m_knownRegions.
        /// </summary>
        /// <param name="newRegionX">The new region's x on the map</param>
        /// <param name="newRegionY">The new region's y on the map</param>
        /// <returns></returns>
        public void CloseChildAgents(bool logout, ulong newRegionHandle, int newRegionSizeX, int newRegionSizeY)
        {
            uint newRegionX, newRegionY;
            List<ulong> byebyeRegions = new List<ulong>();
            List<ulong> knownRegions = KnownRegionHandles;
            m_log.DebugFormat(
                "[SCENE PRESENCE]: Closing child agents. Checking {0} regions in {1}",
                knownRegions.Count, Scene.RegionInfo.RegionName);
            //DumpKnownRegions();

            Util.RegionHandleToRegionLoc(newRegionHandle, out newRegionX, out newRegionY);

            uint x, y;
            spRegionSizeInfo regInfo;

            // this should not be here
            IEventQueue eventQueue = Scene.RequestModuleInterface<IEventQueue>();

            foreach (ulong handle in knownRegions)
            {
                // Don't close the agent on this region yet
                if (handle != Scene.RegionInfo.RegionHandle)
                {
                    if (logout)
                        byebyeRegions.Add(handle);
                    else
                    {
                        Util.RegionHandleToRegionLoc(handle, out x, out y);
                        if (m_knownChildRegionsSizeInfo.TryGetValue(handle, out regInfo))
                        {

                            //                    m_log.Debug("---> x: " + x + "; newx:" + newRegionX + "; Abs:" + (int)Math.Abs((int)(x - newRegionX)));
                            //                    m_log.Debug("---> y: " + y + "; newy:" + newRegionY + "; Abs:" + (int)Math.Abs((int)(y - newRegionY)));
                            if (Util.IsOutsideView(RegionViewDistance, x, newRegionX, y, newRegionY,
                                regInfo.sizeX, regInfo.sizeY, newRegionSizeX, newRegionSizeY))
                            {
                                byebyeRegions.Add(handle);
                                // this should not be here
//                                if(eventQueue != null)
///                                    eventQueue.DisableSimulator(handle,UUID);
                            }
                        }
                        else
                        {
                            if (Util.IsOutsideView(RegionViewDistance, x, newRegionX, y, newRegionY,
                                (int)Constants.RegionSize, (int)Constants.RegionSize, newRegionSizeX, newRegionSizeY))
                            {
                                byebyeRegions.Add(handle);
                                // this should not be here
//                                if(eventQueue != null)
//                                    eventQueue.DisableSimulator(handle,UUID);
                            }
                        }
                    }
                }
            }

            if (byebyeRegions.Count > 0)
            {
                m_log.Debug("[SCENE PRESENCE]: Closing " + byebyeRegions.Count + " child agents");

                AgentCircuitData acd = Scene.AuthenticateHandler.GetAgentCircuitData(UUID);
                string auth = string.Empty;
                if (acd != null)
                    auth = acd.SessionID.ToString();
                m_scene.SceneGridService.SendCloseChildAgentConnections(ControllingClient.AgentId, auth, byebyeRegions);
            }

            foreach (ulong handle in byebyeRegions)
            {
                RemoveNeighbourRegion(handle);
                Scene.CapsModule.DropChildSeed(UUID, handle);
            }
        }

        #endregion

        /// <summary>
        /// This allows the Sim owner the abiility to kick users from their sim currently.
        /// It tells the client that the agent has permission to do so.
        /// </summary>
        public void GrantGodlikePowers(UUID agentID, UUID sessionID, UUID token, bool godStatus)
        {
            int oldgodlevel = GodLevel;

            if (godStatus)
            {
                // For now, assign god level 200 to anyone
                // who is granted god powers, but has no god level set.
                //
                UserAccount account = m_scene.UserAccountService.GetUserAccount(m_scene.RegionInfo.ScopeID, agentID);
                if (account != null)
                {
                    if (account.UserLevel > 0)
                        GodLevel = account.UserLevel;
                    else
                        GodLevel = 200;
                }
            }
            else
            {
                GodLevel = 0;
            }

            ControllingClient.SendAdminResponse(token, (uint)GodLevel);

            if(oldgodlevel != GodLevel)
                parcelGodCheck(m_currentParcelUUID, GodLevel >= 200);
        }

        #region Child Agent Updates

        public void UpdateChildAgent(AgentData cAgentData)
        {
//            m_log.Debug("   >>> ChildAgentDataUpdate <<< " + Scene.RegionInfo.RegionName);
            if (!IsChildAgent)
                return;

            CopyFrom(cAgentData);
             
        }

        private static Vector3 marker = new Vector3(-1f, -1f, -1f);

        private void RaiseUpdateThrottles()
        {
            m_scene.EventManager.TriggerThrottleUpdate(this);
        }

        /// <summary>
        /// This updates important decision making data about a child agent
        /// The main purpose is to figure out what objects to send to a child agent that's in a neighboring region
        /// </summary>
        public void UpdateChildAgent(AgentPosition cAgentData, uint tRegionX, uint tRegionY, uint rRegionX, uint rRegionY)
        {
            if (!IsChildAgent)
                return;

            RegionHandle = cAgentData.RegionHandle;

            //m_log.Debug("   >>> ChildAgentPositionUpdate <<< " + rRegionX + "-" + rRegionY);
            int shiftx = ((int)rRegionX - (int)tRegionX) * (int)Constants.RegionSize;
            int shifty = ((int)rRegionY - (int)tRegionY) * (int)Constants.RegionSize;

            Vector3 offset = new Vector3(shiftx, shifty, 0f);

            DrawDistance = cAgentData.Far;

            if (cAgentData.Position != marker) // UGH!!
                m_pos = cAgentData.Position + offset;

            CameraPosition = cAgentData.Center + offset;

            if ((cAgentData.Throttles != null) && cAgentData.Throttles.Length > 0)
            {
                // some scaling factor
                float x = m_pos.X;
                if (x > m_scene.RegionInfo.RegionSizeX)
                    x -= m_scene.RegionInfo.RegionSizeX;
                float y = m_pos.Y;
                if (y > m_scene.RegionInfo.RegionSizeY)
                    y -= m_scene.RegionInfo.RegionSizeY;

                x = x * x + y * y;

                const float distScale = 0.4f / Constants.RegionSize / Constants.RegionSize;
                float factor = 1.0f - distScale * x;
                if (factor < 0.2f)
                    factor = 0.2f;

                ControllingClient.SetChildAgentThrottle(cAgentData.Throttles,factor);
            }

            if(cAgentData.ChildrenCapSeeds != null && cAgentData.ChildrenCapSeeds.Count >0)
            {
                if (Scene.CapsModule != null)
                {
                    Scene.CapsModule.SetChildrenSeed(UUID, cAgentData.ChildrenCapSeeds);
                }

                KnownRegions = cAgentData.ChildrenCapSeeds;
            }

            //cAgentData.AVHeight;
            //m_velocity = cAgentData.Velocity;
            checkRePrioritization();
        }

        public void CopyTo(AgentData cAgent)
        {
            cAgent.CallbackURI = m_callbackURI;

            cAgent.AgentID = UUID;
            cAgent.RegionID = Scene.RegionInfo.RegionID;
            cAgent.SessionID = ControllingClient.SessionId;

            cAgent.Position = AbsolutePosition;
            cAgent.Velocity = m_velocity;
            cAgent.Center = CameraPosition;
            cAgent.AtAxis = CameraAtAxis;
            cAgent.LeftAxis = CameraLeftAxis;
            cAgent.UpAxis = CameraUpAxis;

            cAgent.Far = DrawDistance;

            // Throttles 
            cAgent.Throttles = ControllingClient.GetThrottlesPacked(1);

            cAgent.HeadRotation = m_headrotation;
            cAgent.BodyRotation = Rotation;
            cAgent.ControlFlags = (uint)m_AgentControlFlags;

            if (m_scene.Permissions.IsGod(new UUID(cAgent.AgentID)))
                cAgent.GodLevel = (byte)GodLevel;
            else 
                cAgent.GodLevel = (byte) 0;

            cAgent.AlwaysRun = SetAlwaysRun;

            // make clear we want the all thing
            cAgent.Appearance = new AvatarAppearance(Appearance,true,true);

            cAgent.ParentPart = ParentUUID;
            cAgent.SitOffset = PrevSitOffset;
            
            lock (scriptedcontrols)
            {
                ControllerData[] controls = new ControllerData[scriptedcontrols.Count];
                int i = 0;

                foreach (ScriptControllers c in scriptedcontrols.Values)
                {
                    controls[i++] = new ControllerData(c.objectID, c.itemID, (uint)c.ignoreControls, (uint)c.eventControls);
                }
                cAgent.Controllers = controls;
            }

            // Animations
            try
            {
                cAgent.Anims = Animator.Animations.ToArray();
            }
            catch { }
            cAgent.DefaultAnim = Animator.Animations.DefaultAnimation;
            cAgent.AnimState = Animator.Animations.ImplicitDefaultAnimation;

            cAgent.MovementAnimationOverRides = Overrides.CloneAOPairs();

            cAgent.MotionState = (byte)Animator.currentControlState;

            if (Scene.AttachmentsModule != null)
                Scene.AttachmentsModule.CopyAttachments(this, cAgent);
        }

        private void CopyFrom(AgentData cAgent)
        {
            m_callbackURI = cAgent.CallbackURI;
//            m_log.DebugFormat(
//                "[SCENE PRESENCE]: Set callback for {0} in {1} to {2} in CopyFrom()",
//                Name, m_scene.RegionInfo.RegionName, m_callbackURI);

            m_pos = cAgent.Position;
            m_velocity = cAgent.Velocity;
            CameraPosition = cAgent.Center;
            CameraAtAxis = cAgent.AtAxis;
            CameraLeftAxis = cAgent.LeftAxis;
            CameraUpAxis = cAgent.UpAxis;
            ParentUUID = cAgent.ParentPart;
            PrevSitOffset = cAgent.SitOffset;

            // When we get to the point of re-computing neighbors everytime this
            // changes, then start using the agent's drawdistance rather than the 
            // region's draw distance.
            DrawDistance = cAgent.Far;
            //DrawDistance = Scene.DefaultDrawDistance;

            if (cAgent.ChildrenCapSeeds != null && cAgent.ChildrenCapSeeds.Count > 0)
            {
                if (Scene.CapsModule != null)
                {
                    Scene.CapsModule.SetChildrenSeed(UUID, cAgent.ChildrenCapSeeds);
                }
                KnownRegions = cAgent.ChildrenCapSeeds;
            }

            if ((cAgent.Throttles != null) && cAgent.Throttles.Length > 0)
                ControllingClient.SetChildAgentThrottle(cAgent.Throttles);

            m_headrotation = cAgent.HeadRotation;
            Rotation = cAgent.BodyRotation;
            m_AgentControlFlags = (AgentManager.ControlFlags)cAgent.ControlFlags; 

            if (m_scene.Permissions.IsGod(new UUID(cAgent.AgentID)))
                GodLevel = cAgent.GodLevel;
            SetAlwaysRun = cAgent.AlwaysRun;


            Appearance = new AvatarAppearance(cAgent.Appearance);
/*
            bool isFlying = ((m_AgentControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_FLY) != 0); 

            if (PhysicsActor != null)
            {
                RemoveFromPhysicalScene();
                AddToPhysicalScene(isFlying);
            }
*/            
            try
            {
                lock (scriptedcontrols)
                {
                    if (cAgent.Controllers != null)
                    {
                        scriptedcontrols.Clear();

                        foreach (ControllerData c in cAgent.Controllers)
                        {
                            ScriptControllers sc = new ScriptControllers();
                            sc.objectID = c.ObjectID;
                            sc.itemID = c.ItemID;
                            sc.ignoreControls = (ScriptControlled)c.IgnoreControls;
                            sc.eventControls = (ScriptControlled)c.EventControls;

                            scriptedcontrols[sc.itemID] = sc;
                        }
                    }
                }
            }
            catch { }

            Animator.ResetAnimations();

            Overrides.CopyAOPairsFrom(cAgent.MovementAnimationOverRides);

            // FIXME: Why is this null check necessary?  Where are the cases where we get a null Anims object?
            if (cAgent.DefaultAnim != null)
                Animator.Animations.SetDefaultAnimation(cAgent.DefaultAnim.AnimID, cAgent.DefaultAnim.SequenceNum, UUID.Zero);
            if (cAgent.AnimState != null)
                Animator.Animations.SetImplicitDefaultAnimation(cAgent.AnimState.AnimID, cAgent.AnimState.SequenceNum, UUID.Zero);
            if (cAgent.Anims != null)
                Animator.Animations.FromArray(cAgent.Anims);
            if (cAgent.MotionState != 0)
                Animator.currentControlState = (ScenePresenceAnimator.motionControlStates) cAgent.MotionState;

            if (Scene.AttachmentsModule != null)
                Scene.AttachmentsModule.CopyAttachments(cAgent, this);

            lock (m_originRegionIDAccessLock)
                m_originRegionID = cAgent.RegionID;
        }

        public bool CopyAgent(out IAgentData agent)
        {
            agent = new CompleteAgentData();
            CopyTo((AgentData)agent);
            return true;
        }

        #endregion Child Agent Updates

        /// <summary>
        /// Handles part of the PID controller function for moving an avatar.
        /// </summary>
        public void UpdateMovement()
        {
            if (IsInTransit)
                return;
            if (m_forceToApply.HasValue)
            {
                Vector3 force = m_forceToApply.Value;

                Velocity = force;

                m_forceToApply = null;
                TriggerScenePresenceUpdated();
            }
        }

        /// <summary>
        /// Adds a physical representation of the avatar to the Physics plugin
        /// </summary>
        public void AddToPhysicalScene(bool isFlying)
        {
//            m_log.DebugFormat(
//                "[SCENE PRESENCE]: Adding physics actor for {0}, ifFlying = {1} in {2}",
//                Name, isFlying, Scene.RegionInfo.RegionName);

            if (PhysicsActor != null)
            {
                m_log.ErrorFormat(
                    "[SCENE PRESENCE]: Adding physics actor for {0} to {1} but this scene presence already has a physics actor",
                    Name, Scene.RegionInfo.RegionName);
            }

            if (Appearance.AvatarHeight == 0)
//                Appearance.SetHeight();
                Appearance.SetSize(new Vector3(0.45f,0.6f,1.9f));

            PhysicsScene scene = m_scene.PhysicsScene;
            Vector3 pVec = AbsolutePosition;                  

            PhysicsActor = scene.AddAvatar(
                LocalId, Firstname + "." + Lastname, pVec,
                Appearance.AvatarBoxSize,Appearance.AvatarFeetOffset, isFlying);
            PhysicsActor.Orientation = m_bodyRot;
            //PhysicsActor.OnRequestTerseUpdate += SendTerseUpdateToAllClients;
            PhysicsActor.OnCollisionUpdate += PhysicsCollisionUpdate;
            PhysicsActor.OnOutOfBounds += OutOfBoundsCall; // Called for PhysicsActors when there's something wrong
            PhysicsActor.SubscribeEvents(100);
            PhysicsActor.LocalID = LocalId;
        }

        private void OutOfBoundsCall(Vector3 pos)
        {
            //bool flying = Flying;
            //RemoveFromPhysicalScene();

            //AddToPhysicalScene(flying);
            if (ControllingClient != null)
                ControllingClient.SendAgentAlertMessage("Physics is having a problem with your avatar.  You may not be able to move until you relog.", true);
        }


        /// <summary>
        /// Event called by the physics plugin to tell the avatar about a collision.
        /// </summary>
        /// <remarks>
        /// This function is called continuously, even when there are no collisions.  If the avatar is walking on the
        /// ground or a prim then there will be collision information between the avatar and the surface.
        ///
        /// FIXME: However, we can't safely avoid calling this yet where there are no collisions without analyzing whether
        /// any part of this method is relying on an every-frame call.
        /// </remarks>
        /// <param name="e"></param>
        public void PhysicsCollisionUpdate(EventArgs e)
        {
            if (IsChildAgent || Animator == null)
                return;

            if(IsInTransit)
                return;
            //if ((Math.Abs(Velocity.X) > 0.1e-9f) || (Math.Abs(Velocity.Y) > 0.1e-9f))
            // The Physics Scene will send updates every 500 ms grep: PhysicsActor.SubscribeEvents(
            // as of this comment the interval is set in AddToPhysicalScene

//                if (m_updateCount > 0)
//                {
            if (Animator.UpdateMovementAnimations())
                TriggerScenePresenceUpdated();
//                    m_updateCount--;
//                }

            CollisionEventUpdate collisionData = (CollisionEventUpdate)e;
            Dictionary<uint, ContactPoint> coldata = collisionData.m_objCollisionList;


//            // No collisions at all means we may be flying. Update always
//            // to make falling work
//            if (m_lastColCount != coldata.Count || coldata.Count == 0)
//            {
//                m_updateCount = UPDATE_COUNT;
//                m_lastColCount = coldata.Count;
//            }

            if (coldata.Count != 0)
            {
/*
                switch (Animator.CurrentMovementAnimation)
                {
                    case "STAND":
                    case "WALK":
                    case "RUN":
                    case "CROUCH":
                    case "CROUCHWALK":
                        {
 */
                            ContactPoint lowest;
                            lowest.SurfaceNormal = Vector3.Zero;
                            lowest.Position = Vector3.Zero;
                            lowest.Position.Z = float.MaxValue;

                            foreach (ContactPoint contact in coldata.Values)
                            {
                                
                                if (contact.CharacterFeet && contact.Position.Z < lowest.Position.Z)
                                {
                                    lowest = contact;
                                }
                            }

                            if (lowest.Position.Z != float.MaxValue)
                            {
                                lowest.SurfaceNormal = -lowest.SurfaceNormal;
                                CollisionPlane = new Vector4(lowest.SurfaceNormal, Vector3.Dot(lowest.Position, lowest.SurfaceNormal));
                            }
                            else
                                CollisionPlane = Vector4.UnitW;
/*
                        }
                        break;
                }
*/
            }
            else
                CollisionPlane = Vector4.UnitW;

            RaiseCollisionScriptEvents(coldata);

            // Gods do not take damage and Invulnerable is set depending on parcel/region flags
            if (Invulnerable || GodLevel > 0)
                return;

            // The following may be better in the ICombatModule
            // probably tweaking of the values for ground and normal prim collisions will be needed
            float starthealth = Health;
            uint killerObj = 0;
            SceneObjectPart part = null;
            foreach (uint localid in coldata.Keys)
            {
                if (localid == 0)
                {
                    part = null;
                }
                else
                {
                    part = Scene.GetSceneObjectPart(localid);
                }
                if (part != null)
                {
                    // Ignore if it has been deleted or volume detect
                    if (!part.ParentGroup.IsDeleted && !part.ParentGroup.IsVolumeDetect)
                    {
                        if (part.ParentGroup.Damage > 0.0f)
                        {
                            // Something with damage...
                            Health -= part.ParentGroup.Damage;
                            part.ParentGroup.Scene.DeleteSceneObject(part.ParentGroup, false);
                        }
                        else
                        {
                            // An ordinary prim
                            if (coldata[localid].PenetrationDepth >= 0.10f)
                                Health -= coldata[localid].PenetrationDepth * 5.0f;
                        }
                    }
                }
                else
                {
                    // 0 is the ground
                    // what about collisions with other avatars?
                    if (localid == 0 && coldata[localid].PenetrationDepth >= 0.10f)
                        Health -= coldata[localid].PenetrationDepth * 5.0f;
                }


                if (Health <= 0.0f)
                {
                    if (localid != 0)
                        killerObj = localid;
                }
                //m_log.Debug("[AVATAR]: Collision with localid: " + localid.ToString() + " at depth: " + coldata[localid].ToString());
            }
            //Health = 100;
            if (!Invulnerable)
            {
                if (starthealth != Health)
                {
                    ControllingClient.SendHealth(Health);
                }
                if (Health <= 0)
                {
                    m_scene.EventManager.TriggerAvatarKill(killerObj, this);
                }
                if (starthealth == Health && Health < 100.0f)
                {
                    Health += 0.03f;
                    if (Health > 100.0f)
                        Health = 100.0f;
                    ControllingClient.SendHealth(Health);
                }
            }
        }

        public void setHealthWithUpdate(float health)
        {
            Health = health;
            ControllingClient.SendHealth(Health);
        }

        protected internal void Close()
        {
            // Clear known regions
            KnownRegions = new Dictionary<ulong, string>();
            
            // I don't get it but mono crashes when you try to dispose of this timer,
            // unsetting the elapsed callback should be enough to allow for cleanup however.
            // m_reprioritizationTimer.Dispose(); 

            RemoveFromPhysicalScene();
          
            m_scene.EventManager.OnRegionHeartbeatEnd -= RegionHeartbeatEnd;

//            if (Animator != null)
//                Animator.Close();
            Animator = null;

            LifecycleState = ScenePresenceState.Removed;
        }

        public void AddAttachment(SceneObjectGroup gobj)
        {
            lock (m_attachments)
            {
                // This may be true when the attachment comes back
                // from serialization after login. Clear it.
                gobj.IsDeleted = false;

                m_attachments.Add(gobj);
            }

            IBakedTextureModule bakedModule = m_scene.RequestModuleInterface<IBakedTextureModule>();
            if (bakedModule != null)
                bakedModule.UpdateMeshAvatar(m_uuid);
        }

        /// <summary>
        /// Get all the presence's attachments.
        /// </summary>
        /// <returns>A copy of the list which contains the attachments.</returns>
        public List<SceneObjectGroup> GetAttachments()
        {
            lock (m_attachments)
                return new List<SceneObjectGroup>(m_attachments);
        }

        /// <summary>
        /// Get the scene objects attached to the given point.
        /// </summary>
        /// <param name="attachmentPoint"></param>
        /// <returns>Returns an empty list if there were no attachments at the point.</returns>
        public List<SceneObjectGroup> GetAttachments(uint attachmentPoint)
        {
            List<SceneObjectGroup> attachments = new List<SceneObjectGroup>();

            if (attachmentPoint >= 0)
            {
                lock (m_attachments)
                {
                    foreach (SceneObjectGroup so in m_attachments)
                    {
                        if (attachmentPoint == so.AttachmentPoint)
                            attachments.Add(so);
                    }
                }
            }
            
            return attachments;
        }

        public bool HasAttachments()
        {
            lock (m_attachments)
                return m_attachments.Count > 0;
        }

        /// <summary>
        /// Returns the total count of scripts in all parts inventories.
        /// </summary>
        public int ScriptCount()
        {
            int count = 0;
            lock (m_attachments)
            {
                foreach (SceneObjectGroup gobj in m_attachments)
                {
                    if (gobj != null)
                    {
                        count += gobj.ScriptCount();
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// A float the value is a representative execution time in milliseconds of all scripts in all attachments.
        /// </summary>
        public float ScriptExecutionTime()
        {
            float time = 0.0f;
            lock (m_attachments)
            {
                foreach (SceneObjectGroup gobj in m_attachments)
                {
                    if (gobj != null)
                    {
                        time += gobj.ScriptExecutionTime();
                    }
                }
            }
            return time;
        }

        /// <summary>
        /// Returns the total count of running scripts in all parts.
        /// </summary>
        public int RunningScriptCount()
        {
            int count = 0;
            lock (m_attachments)
            {
                foreach (SceneObjectGroup gobj in m_attachments)
                {
                    if (gobj != null)
                    {
                        count += gobj.RunningScriptCount();
                    }
                }
            }
            return count;
        }

        public bool HasScriptedAttachments()
        {
            lock (m_attachments)
            {
                foreach (SceneObjectGroup gobj in m_attachments)
                {
                    if (gobj != null)
                    {
                        if (gobj.RootPart.Inventory.ContainsScripts())
                            return true;
                    }
                }
            }
            return false;
        }

        public void RemoveAttachment(SceneObjectGroup gobj)
        {
            lock (m_attachments)
                m_attachments.Remove(gobj);
        }

        /// <summary>
        /// Clear all attachments
        /// </summary>
        public void ClearAttachments()
        {
            lock (m_attachments)
                m_attachments.Clear();
        }

        /// <summary>
        /// This is currently just being done for information.
        /// </summary>
        public bool ValidateAttachments()
        {
            bool validated = true;

            lock (m_attachments)
            {
                // Validate
                foreach (SceneObjectGroup gobj in m_attachments)
                {
                    if (gobj == null)
                    {
                        m_log.WarnFormat(
                            "[SCENE PRESENCE]: Failed to validate an attachment for {0} since it was null.  Continuing", Name);

                        validated = false;
                    }
                    else if (gobj.IsDeleted)
                    {
                        m_log.WarnFormat(
                            "[SCENE PRESENCE]: Failed to validate attachment {0} {1} for {2} since it had been deleted.  Continuing",
                            gobj.Name, gobj.UUID, Name);

                        validated = false;
                    }
                }
            }

            return validated;
        }

        public void SendAttachmentsToAllAgents()
        {
            lock (m_attachments)
            {
                foreach (SceneObjectGroup sog in m_attachments)
                {
                    m_scene.ForEachScenePresence(delegate(ScenePresence p)
                    {
                        if (p != this && sog.HasPrivateAttachmentPoint)
                            return;

                        if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && p.GodLevel < 200)
                            return;

                        SendTerseUpdateToAgentNF(p);
                        SendAttachmentFullUpdateToAgentNF(sog, p);
                    });
                }
            }
        }

        // send attachments to a client without filters except for huds
        // for now they are checked in several places down the line...
        public void SendAttachmentsToAgentNF(ScenePresence p)
        {
            SendTerseUpdateToAgentNF(p);
//            SendAvatarDataToAgentNF(this);
            lock (m_attachments)
            {
                foreach (SceneObjectGroup sog in m_attachments)
                {
                    SendAttachmentFullUpdateToAgentNF(sog, p);
                }               
            }
        }

        public void SendAttachmentFullUpdateToAgentNF(SceneObjectGroup sog, ScenePresence p)
        {
            if (p != this && sog.HasPrivateAttachmentPoint)
                return;

            SceneObjectPart[] parts = sog.Parts;
            SceneObjectPart rootpart = sog.RootPart;

            p.ControllingClient.SendEntityUpdate(rootpart, PrimUpdateFlags.FullUpdate);

            for (int i = 0; i < parts.Length; i++)
            {
                SceneObjectPart part = parts[i];
                if (part == rootpart)
                    continue;
                p.ControllingClient.SendEntityUpdate(part, PrimUpdateFlags.FullUpdate);
            }
        }

        public void SendAttachmentScheduleUpdate(SceneObjectGroup sog)
        {
            if (IsChildAgent || IsInTransit)
                return;

            SceneObjectPart[] origparts = sog.Parts;
            SceneObjectPart[] parts = new SceneObjectPart[origparts.Length];
            PrimUpdateFlags[] flags = new PrimUpdateFlags[origparts.Length];

            SceneObjectPart rootpart = sog.RootPart;
            UpdateRequired rootreq = sog.RootPart.UpdateFlag;

            int j = 0;
            bool allterse = true;
            for (int i = 0; i < origparts.Length; i++)
            {
                if (origparts[i] != rootpart)
                {
                    switch (origparts[i].UpdateFlag)
                    {
                        case UpdateRequired.NONE:
                            break;

                        case UpdateRequired.TERSE:
                            flags[j] = PrimUpdateFlags.Position | PrimUpdateFlags.Rotation | PrimUpdateFlags.Velocity
                            | PrimUpdateFlags.Acceleration | PrimUpdateFlags.AngularVelocity;
                            parts[j] = origparts[i];
                            j++;
                            break;

                        case UpdateRequired.FULL:
                            flags[j] = PrimUpdateFlags.FullUpdate;
                            allterse = false;
                            parts[j] = origparts[i];
                            j++;
                            break;
                    }
                }
                origparts[i].UpdateFlag = 0;
            }

            if (j == 0 && rootreq == UpdateRequired.NONE)
                return;

            PrimUpdateFlags rootflag = PrimUpdateFlags.FullUpdate;

            if (rootreq != UpdateRequired.FULL && allterse)
            {
                rootflag = PrimUpdateFlags.Position | PrimUpdateFlags.Rotation | PrimUpdateFlags.Velocity
                        | PrimUpdateFlags.Acceleration | PrimUpdateFlags.AngularVelocity;
            }

            int nparts = j;

            ControllingClient.SendEntityUpdate(rootpart, rootflag);

            for (int i = 0; i < nparts; i++)
            {
                ControllingClient.SendEntityUpdate(parts[i], flags[i]);
            }

            if (sog.HasPrivateAttachmentPoint)
                return;

            List<ScenePresence> allPresences = m_scene.GetScenePresences();
            foreach (ScenePresence p in allPresences)
            {
                if (p == this)
                    continue;

                if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && p.GodLevel < 200)
                    continue;

                p.ControllingClient.SendEntityUpdate(rootpart, rootflag);

                for (int i = 0; i < nparts; i++)
                {
                    p.ControllingClient.SendEntityUpdate(parts[i], flags[i]);
                }
            }
        }

        public void SendAttachmentUpdate(SceneObjectGroup sog, UpdateRequired UpdateFlag)
        {
            if (IsChildAgent || IsInTransit)
                return;

            PrimUpdateFlags flag;
            switch (UpdateFlag)
            {
                case UpdateRequired.TERSE:
                    flag = PrimUpdateFlags.Position | PrimUpdateFlags.Rotation | PrimUpdateFlags.Velocity
                    | PrimUpdateFlags.Acceleration | PrimUpdateFlags.AngularVelocity;
                    break;

                case UpdateRequired.FULL:
                    flag = PrimUpdateFlags.FullUpdate;
                    break;

                default:
                    return;
            }

            SceneObjectPart[] parts = sog.Parts;
            SceneObjectPart rootpart = sog.RootPart;

//            rootpart.UpdateFlag = 0;

            ControllingClient.SendEntityUpdate(rootpart, flag);

            for (int i = 0; i < parts.Length; i++)
            {
                SceneObjectPart part = parts[i];
                if (part == rootpart)
                    continue;
                ControllingClient.SendEntityUpdate(part, flag);
//                part.UpdateFlag = 0;
            }

            if (sog.HasPrivateAttachmentPoint)
                return;


            List<ScenePresence> allPresences = m_scene.GetScenePresences();
            foreach (ScenePresence p in allPresences)
            {
                if (p == this)
                    continue;

                if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && p.GodLevel < 200)
                    continue;

                p.ControllingClient.SendEntityUpdate(rootpart, flag);

                for (int i = 0; i < parts.Length; i++)
                {
                    SceneObjectPart part = parts[i];
                    if (part == rootpart)
                        continue;
                    p.ControllingClient.SendEntityUpdate(part, flag);
                }
            }
        }

        public void SendAttachmentScheduleUpdate(SceneObjectPart part)
        {
            if (IsChildAgent || IsInTransit)
                return;


            PrimUpdateFlags flag;
            switch (part.UpdateFlag)
            {
                case UpdateRequired.TERSE:
                    flag = PrimUpdateFlags.Position | PrimUpdateFlags.Rotation | PrimUpdateFlags.Velocity
                    | PrimUpdateFlags.Acceleration | PrimUpdateFlags.AngularVelocity;
                    break;

                case UpdateRequired.FULL:
                    flag = PrimUpdateFlags.FullUpdate;
                    break;

                default:
                    return;
            }

            part.UpdateFlag = 0;

            ControllingClient.SendEntityUpdate(part, flag);

            if (part.ParentGroup.HasPrivateAttachmentPoint)
                return;

            List<ScenePresence> allPresences = m_scene.GetScenePresences();
            foreach (ScenePresence p in allPresences)
            {
                if (p == this)
                    continue;

                if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && p.GodLevel < 200)
                    continue;

                p.ControllingClient.SendEntityUpdate(part, flag);
            }
        }


        public void SendAttachmentUpdate(SceneObjectPart part, UpdateRequired UpdateFlag)
        {
            if (IsChildAgent || IsInTransit)
                return;

            PrimUpdateFlags flag;
            switch (UpdateFlag)
            {
                case UpdateRequired.TERSE:
                    flag = PrimUpdateFlags.Position | PrimUpdateFlags.Rotation | PrimUpdateFlags.Velocity
                    | PrimUpdateFlags.Acceleration | PrimUpdateFlags.AngularVelocity;
                    break;

                case UpdateRequired.FULL:
                    flag = PrimUpdateFlags.FullUpdate;
                    break;

                default:
                    return;
            }

//            part.UpdateFlag = 0;

            ControllingClient.SendEntityUpdate(part, flag);

            if (part.ParentGroup.HasPrivateAttachmentPoint)
                return;

            List<ScenePresence> allPresences = m_scene.GetScenePresences();
            foreach (ScenePresence p in allPresences)
            {
                if (p == this)
                    continue;
                if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && p.GodLevel < 200)
                    continue;

                p.ControllingClient.SendEntityUpdate(part, flag);
            }
        }

        /// <summary>
        /// Send a script event to this scene presence's attachments
        /// </summary>
        /// <param name="eventName">The name of the event</param>
        /// <param name="args">The arguments for the event</param>
        public void SendScriptEventToAttachments(string eventName, Object[] args)
        {
            Util.FireAndForget(delegate(object x)
            {
                if (m_scriptEngines.Length == 0)
                    return;

                lock (m_attachments)
                {
                    foreach (SceneObjectGroup grp in m_attachments)
                    {
                        // 16384 is CHANGED_ANIMATION
                        //
                        // Send this to all attachment root prims
                        //
                        foreach (IScriptModule m in m_scriptEngines)
                        {
                            if (m == null) // No script engine loaded
                                continue;

                            m.PostObjectEvent(grp.RootPart.UUID, "changed", new Object[] { (int)Changed.ANIMATION });
                        }
                    }
                }
            }, null, "ScenePresence.SendScriptEventToAttachments");
        }

        /// <summary>
        /// Gets the mass.
        /// </summary>
        /// <returns>
        /// The mass.
        /// </returns>
        public float GetMass()
        {
            PhysicsActor pa = PhysicsActor;

            if (pa != null)
                return pa.Mass;
            else
                return 0;
        }

        internal void PushForce(Vector3 impulse)
        {
            if (PhysicsActor != null)
            {
                PhysicsActor.AddForce(impulse,true);
            }
        }

        public void RegisterControlEventsToScript(int controls, int accept, int pass_on, uint Obj_localID, UUID Script_item_UUID)
        {
            SceneObjectPart p = m_scene.GetSceneObjectPart(Obj_localID);
            if (p == null)
                return;

            ControllingClient.SendTakeControls(controls, false, false);
            ControllingClient.SendTakeControls(controls, true, false);

            ScriptControllers obj = new ScriptControllers();
            obj.ignoreControls = ScriptControlled.CONTROL_ZERO;
            obj.eventControls = ScriptControlled.CONTROL_ZERO;

            obj.objectID = p.ParentGroup.UUID;
            obj.itemID = Script_item_UUID;
            if (pass_on == 0 && accept == 0)
            {
                IgnoredControls |= (ScriptControlled)controls;
                obj.ignoreControls = (ScriptControlled)controls;
            }

            if (pass_on == 0 && accept == 1)
            {
                IgnoredControls |= (ScriptControlled)controls;
                obj.ignoreControls = (ScriptControlled)controls;
                obj.eventControls = (ScriptControlled)controls;
            }

            if (pass_on == 1 && accept == 1)
            {
                IgnoredControls = ScriptControlled.CONTROL_ZERO;
                obj.eventControls = (ScriptControlled)controls;
                obj.ignoreControls = ScriptControlled.CONTROL_ZERO;
            }

            lock (scriptedcontrols)
            {
                if (pass_on == 1 && accept == 0)
                {
                    IgnoredControls &= ~(ScriptControlled)controls;
                    if (scriptedcontrols.ContainsKey(Script_item_UUID))
                        scriptedcontrols.Remove(Script_item_UUID);
                }
                else
                {
                    scriptedcontrols[Script_item_UUID] = obj;
                }
            }

            ControllingClient.SendTakeControls(controls, pass_on == 1 ? true : false, true);
        }

        public void HandleForceReleaseControls(IClientAPI remoteClient, UUID agentID)
        {
            IgnoredControls = ScriptControlled.CONTROL_ZERO;
            lock (scriptedcontrols)
            {
                scriptedcontrols.Clear();
            }
            ControllingClient.SendTakeControls(int.MaxValue, false, false);
        }

        public void ClearControls()
        {
            IgnoredControls = ScriptControlled.CONTROL_ZERO;
            lock (scriptedcontrols)
            {
                scriptedcontrols.Clear();
            }
        }

        public void UnRegisterSeatControls(UUID obj)
        {
            List<UUID> takers = new List<UUID>();

            foreach (ScriptControllers c in scriptedcontrols.Values)
            {
                if (c.objectID == obj)
                    takers.Add(c.itemID);
            }
            foreach (UUID t in takers)
            {
                UnRegisterControlEventsToScript(0, t);
            }
        }

        public void UnRegisterControlEventsToScript(uint Obj_localID, UUID Script_item_UUID)
        {
            ScriptControllers takecontrols;

            lock (scriptedcontrols)
            {
                if (scriptedcontrols.TryGetValue(Script_item_UUID, out takecontrols))
                {
                    ScriptControlled sctc = takecontrols.eventControls;

                    ControllingClient.SendTakeControls((int)sctc, false, false);
                    ControllingClient.SendTakeControls((int)sctc, true, false);

                    scriptedcontrols.Remove(Script_item_UUID);
                    IgnoredControls = ScriptControlled.CONTROL_ZERO;
                    foreach (ScriptControllers scData in scriptedcontrols.Values)
                    {
                        IgnoredControls |= scData.ignoreControls;
                    }
                }
            }
        }

        private void SendControlsToScripts(uint flags)
        {
            // Notify the scripts only after calling UpdateMovementAnimations(), so that if a script
            // (e.g., a walking script) checks which animation is active it will be the correct animation.
            lock (scriptedcontrols)
            {
                if (scriptedcontrols.Count <= 0)
                    return;

                ScriptControlled allflags = ScriptControlled.CONTROL_ZERO;
    
                if (MouseDown)
                {
                    allflags = LastCommands & (ScriptControlled.CONTROL_ML_LBUTTON | ScriptControlled.CONTROL_LBUTTON);
                    if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_UP) != 0 || (flags & unchecked((uint)AgentManager.ControlFlags.AGENT_CONTROL_ML_LBUTTON_UP)) != 0)
                    {
                        allflags = ScriptControlled.CONTROL_ZERO;
                        MouseDown = true;
                    }
                }
    
                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_ML_LBUTTON_DOWN) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_ML_LBUTTON;
                    MouseDown = true;
                }
    
                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_DOWN) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_LBUTTON;
                    MouseDown = true;
                }
    
                // find all activated controls, whether the scripts are interested in them or not
                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_POS) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_FWD;
                }
    
                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_NEG) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_BACK;
                }
    
                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_POS) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_UP;
                }
    
                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_DOWN;
                }

                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_POS) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_LEFT;
                }
    
                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_NEG) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_RIGHT;
                }
    
                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_YAW_NEG) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_ROT_RIGHT;
                }
    
                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_YAW_POS) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_ROT_LEFT;
                }

                // optimization; we have to check per script, but if nothing is pressed and nothing changed, we can skip that
                if (allflags != ScriptControlled.CONTROL_ZERO || allflags != LastCommands)
                {
                    foreach (KeyValuePair<UUID, ScriptControllers> kvp in scriptedcontrols)
                    {
                        UUID scriptUUID = kvp.Key;
                        ScriptControllers scriptControlData = kvp.Value;
    
                        ScriptControlled localHeld = allflags & scriptControlData.eventControls;     // the flags interesting for us
                        ScriptControlled localLast = LastCommands & scriptControlData.eventControls; // the activated controls in the last cycle
                        ScriptControlled localChange = localHeld ^ localLast;                        // the changed bits

                        if (localHeld != ScriptControlled.CONTROL_ZERO || localChange != ScriptControlled.CONTROL_ZERO)
                        {
                            // only send if still pressed or just changed
                            m_scene.EventManager.TriggerControlEvent(scriptUUID, UUID, (uint)localHeld, (uint)localChange);
                        }
                    }
                }
    
                LastCommands = allflags;
            }
        }

        internal static AgentManager.ControlFlags RemoveIgnoredControls(AgentManager.ControlFlags flags, ScriptControlled ignored)
        {
            if (ignored == ScriptControlled.CONTROL_ZERO)
                return flags;

            if ((ignored & ScriptControlled.CONTROL_BACK) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG | AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_NEG);
            if ((ignored & ScriptControlled.CONTROL_FWD) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_POS | AgentManager.ControlFlags.AGENT_CONTROL_AT_POS);
            if ((ignored & ScriptControlled.CONTROL_DOWN) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG | AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG);
            if ((ignored & ScriptControlled.CONTROL_UP) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_POS | AgentManager.ControlFlags.AGENT_CONTROL_UP_POS);
            if ((ignored & ScriptControlled.CONTROL_LEFT) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS | AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_POS);
            if ((ignored & ScriptControlled.CONTROL_RIGHT) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_NEG | AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG);
            if ((ignored & ScriptControlled.CONTROL_ROT_LEFT) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_YAW_NEG);
            if ((ignored & ScriptControlled.CONTROL_ROT_RIGHT) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_YAW_POS);
            if ((ignored & ScriptControlled.CONTROL_ML_LBUTTON) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_ML_LBUTTON_DOWN);
            if ((ignored & ScriptControlled.CONTROL_LBUTTON) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_UP | AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_DOWN);

            //DIR_CONTROL_FLAG_FORWARD = AgentManager.ControlFlags.AGENT_CONTROL_AT_POS,
            //DIR_CONTROL_FLAG_BACK = AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG,
            //DIR_CONTROL_FLAG_LEFT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS,
            //DIR_CONTROL_FLAG_RIGHT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG,
            //DIR_CONTROL_FLAG_UP = AgentManager.ControlFlags.AGENT_CONTROL_UP_POS,
            //DIR_CONTROL_FLAG_DOWN = AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG,
            //DIR_CONTROL_FLAG_DOWN_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG

            return flags;
        }

        // returns true it local teleport allowed and sets the destiny position into pos
        
        private bool CheckLocalTPLandingPoint(ref Vector3 pos)
        {
            // Never constrain lures
            if ((TeleportFlags & TeleportFlags.ViaLure) != 0)
                return true;

            if (m_scene.RegionInfo.EstateSettings.AllowDirectTeleport)
                return true;

            // do not constrain gods and estate managers
            if(m_scene.Permissions.IsGod(m_uuid) ||
                m_scene.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_uuid))
                return true;

            // will teleport to a telehub spawn point or landpoint if that results in getting closer to target
            // if not the local teleport fails.

            float currDistanceSQ = Vector3.DistanceSquared(AbsolutePosition, pos);

            // first check telehub

            UUID TelehubObjectID = m_scene.RegionInfo.RegionSettings.TelehubObject;
            if ( TelehubObjectID != UUID.Zero)
            {
                SceneObjectGroup telehubSOG =  m_scene.GetSceneObjectGroup(TelehubObjectID);
                if(telehubSOG != null)
                {
                    Vector3 spawnPos;
                    float spawnDistSQ;

                    SpawnPoint[] spawnPoints = m_scene.RegionInfo.RegionSettings.SpawnPoints().ToArray();
                    if(spawnPoints.Length == 0)
                    {
                        spawnPos = new Vector3(128.0f, 128.0f, pos.Z);
                        spawnDistSQ = Vector3.DistanceSquared(spawnPos, pos);
                    }
                    else
                    {
                        Vector3 hubPos = telehubSOG.AbsolutePosition;
                        Quaternion hubRot = telehubSOG.GroupRotation;

                        spawnPos = spawnPoints[0].GetLocation(hubPos, hubRot);
                        spawnDistSQ = Vector3.DistanceSquared(spawnPos, pos);

                        float testDistSQ; 
                        Vector3 testSpawnPos;
                        for(int i = 1; i< spawnPoints.Length; i++)
                        {
                            testSpawnPos = spawnPoints[i].GetLocation(hubPos, hubRot);
                            testDistSQ =  Vector3.DistanceSquared(testSpawnPos, pos);

                            if(testDistSQ < spawnDistSQ)
                            {
                                spawnPos = testSpawnPos;
                                spawnDistSQ = testDistSQ;
                            }
                        }
                    }
                    if (currDistanceSQ < spawnDistSQ)
                    {
                        // we are already close
                        ControllingClient.SendAlertMessage("Can't teleport closer to destination");
                        return false;
                    }
                    else
                    {
                        pos = spawnPos;
                        return true;
                    }
                }
            }

            ILandObject land = m_scene.LandChannel.GetLandObject(pos.X, pos.Y);

            if (land.LandData.LandingType != (byte)LandingType.LandingPoint 
                        || land.LandData.OwnerID == m_uuid)
                return true;

            Vector3 landLocation = land.LandData.UserLocation;
            if(landLocation == Vector3.Zero)
                return true;

            if (currDistanceSQ < Vector3.DistanceSquared(landLocation, pos))
            {
                ControllingClient.SendAlertMessage("Can't teleport closer to destination");
                return false;
            }

            pos = land.LandData.UserLocation;
            return true;
        }

        const TeleportFlags TeleHubTPFlags = TeleportFlags.ViaLogin 
                    | TeleportFlags.ViaHGLogin | TeleportFlags.ViaLocation;

        private bool CheckAndAdjustTelehub(SceneObjectGroup telehub, ref Vector3 pos, ref bool positionChanged)
        {
            // forcing telehubs on any tp that reachs this
            if ((m_teleportFlags & TeleHubTPFlags) != 0 ||
                (m_scene.TelehubAllowLandmarks == true ? false : ((m_teleportFlags & TeleportFlags.ViaLandmark) != 0 )))
            {
                ILandObject land;
                Vector3 teleHubPosition = telehub.AbsolutePosition;

                SpawnPoint[] spawnPoints = m_scene.RegionInfo.RegionSettings.SpawnPoints().ToArray();
                if(spawnPoints.Length == 0)
                {
                    land = m_scene.LandChannel.GetLandObject(teleHubPosition.X,teleHubPosition.Y);
                    if(land != null)
                    {
                        pos = teleHubPosition;
                        if(land.IsEitherBannedOrRestricted(UUID))
                            return false;
                        positionChanged = true;
                        return true;
                    }
                    else
                        return false;
                }

                int index;
                int tries;
                bool selected = false;
                bool validhub = false;
                Vector3 spawnPosition;
                
                Quaternion teleHubRotation = telehub.GroupRotation;

                switch(m_scene.SpawnPointRouting)
                {
                    case "random":
                        tries = spawnPoints.Length;
                        if(tries < 3) // no much sense in random with a few points when there same can have bans
                            goto case "sequence";
                        do
                        {
                            index = Util.RandomClass.Next(spawnPoints.Length - 1);

                            spawnPosition = spawnPoints[index].GetLocation(teleHubPosition, teleHubRotation);
                            land = m_scene.LandChannel.GetLandObject(spawnPosition.X,spawnPosition.Y);
                            if(land != null && !land.IsEitherBannedOrRestricted(UUID))
                                selected = true;

                        } while(selected == false && --tries > 0 );

                        if(tries <= 0)
                            goto case "sequence";
                        
                        pos = spawnPosition;
                        return true;

                    case "sequence":
                        tries = spawnPoints.Length;
                        selected = false; 
                        validhub = false;                      
                        do
                        {
                            index = m_scene.SpawnPoint();
                            spawnPosition = spawnPoints[index].GetLocation(teleHubPosition, teleHubRotation);
                            land = m_scene.LandChannel.GetLandObject(spawnPosition.X,spawnPosition.Y);
                            if(land != null)
                            {
                                validhub = true;
                                if(land.IsEitherBannedOrRestricted(UUID))
                                    selected = false;
                                else
                                    selected = true;
                            }

                        } while(selected == false && --tries > 0);

                        if(!validhub)
                            return false;

                        pos = spawnPosition;

                        if(!selected)
                            return false;
                        positionChanged = true;
                        return true;

                    default:
                    case "closest":
                        float distancesq = float.MaxValue;
                        int closest = -1;
                        validhub = false;

                        for(int i = 0; i < spawnPoints.Length; i++)
                        {
                            spawnPosition = spawnPoints[i].GetLocation(teleHubPosition, teleHubRotation);
                            Vector3 offset = spawnPosition - pos;
                            float dsq = offset.LengthSquared();
                            land = m_scene.LandChannel.GetLandObject(spawnPosition.X,spawnPosition.Y);
                            if(land == null)
                                continue;

                            validhub = true;
                            if(land.IsEitherBannedOrRestricted(UUID))
                                continue;

                            if(dsq >= distancesq)
                                continue;
                            distancesq = dsq;
                            closest = i;
                        }

                        if(!validhub) 
                            return false;

                        if(closest < 0)
                        {
                            pos = spawnPoints[0].GetLocation(teleHubPosition, teleHubRotation);
                            positionChanged = true;
                            return false;
                        }

                        pos = spawnPoints[closest].GetLocation(teleHubPosition, teleHubRotation);
                        positionChanged = true;
                        return true;
                }
            }
            return false;
        }

        const TeleportFlags adicionalLandPointFlags = TeleportFlags.ViaLandmark |
                    TeleportFlags.ViaLocation | TeleportFlags.ViaHGLogin;

        // Modify landing point based on possible banning, telehubs or parcel restrictions.
        // This is the behavior in OpenSim for a very long time, different from SL
        private bool CheckAndAdjustLandingPoint_OS(ref Vector3 pos, ref Vector3 lookat, ref bool positionChanged)
        {
            string reason;

            // Honor bans
            if (!m_scene.TestLandRestrictions(UUID, out reason, ref pos.X, ref pos.Y))
                return false;

            SceneObjectGroup telehub = null;
            if (m_scene.RegionInfo.RegionSettings.TelehubObject != UUID.Zero && (telehub = m_scene.GetSceneObjectGroup(m_scene.RegionInfo.RegionSettings.TelehubObject)) != null)
            {
                if (!m_scene.RegionInfo.EstateSettings.AllowDirectTeleport)
                {
                    CheckAndAdjustTelehub(telehub, ref pos, ref positionChanged);
                    return true;
                }
            }

            ILandObject land = m_scene.LandChannel.GetLandObject(pos.X, pos.Y);
            if (land != null)
            {
                if (Scene.DebugTeleporting)
                    TeleportFlagsDebug();

                // If we come in via login, landmark or map, we want to
                // honor landing points. If we come in via Lure, we want
                // to ignore them.
                if ((m_teleportFlags & (TeleportFlags.ViaLogin | TeleportFlags.ViaRegionID)) ==
                    (TeleportFlags.ViaLogin | TeleportFlags.ViaRegionID) ||
                    (m_teleportFlags & adicionalLandPointFlags) != 0)
                {
                    // Don't restrict gods, estate managers, or land owners to
                    // the TP point. This behaviour mimics agni.
                    if (land.LandData.LandingType == (byte)LandingType.LandingPoint &&
                        land.LandData.UserLocation != Vector3.Zero &&
                        GodLevel < 200 &&
                        ((land.LandData.OwnerID != m_uuid &&
                          !m_scene.Permissions.IsGod(m_uuid) &&
                          !m_scene.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_uuid)) ||
                         (m_teleportFlags & TeleportFlags.ViaLocation) != 0 ||
                         (m_teleportFlags & Constants.TeleportFlags.ViaHGLogin) != 0))
                    {
                        pos = land.LandData.UserLocation;
                    }
                }

                land.SendLandUpdateToClient(ControllingClient);
            }

            return true;
        }

        // Modify landing point based on telehubs or parcel restrictions.
        // This is a behavior coming from AVN, somewhat mimicking SL
        private bool CheckAndAdjustLandingPoint_SL(ref Vector3 pos, ref Vector3 lookat, ref bool positionChanged)
        {
            string reason;

            // dont mess with gods
            if(GodLevel >=  200 || m_scene.Permissions.IsGod(m_uuid))
                return true;

            // respect region owner and managers
//            if(m_scene.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_uuid))
//                return true;

            if (!m_scene.RegionInfo.EstateSettings.AllowDirectTeleport)
            {
                SceneObjectGroup telehub = null;
                if (m_scene.RegionInfo.RegionSettings.TelehubObject != UUID.Zero && (telehub = m_scene.GetSceneObjectGroup(m_scene.RegionInfo.RegionSettings.TelehubObject)) != null)
                {
                    if(CheckAndAdjustTelehub(telehub, ref pos, ref positionChanged))
                        return true;
                }
            }

            // Honor bans, actually we don't honour them
            if (!m_scene.TestLandRestrictions(UUID, out reason, ref pos.X, ref pos.Y))
                return false;

            ILandObject land = m_scene.LandChannel.GetLandObject(pos.X, pos.Y);
            if (land != null)
            {
                if (Scene.DebugTeleporting)
                    TeleportFlagsDebug();

                // If we come in via login, landmark or map, we want to
                // honor landing points. If we come in via Lure, we want
                // to ignore them.
                if ((m_teleportFlags & (TeleportFlags.ViaLogin | TeleportFlags.ViaRegionID)) ==
                                (TeleportFlags.ViaLogin | TeleportFlags.ViaRegionID)
                        || (m_teleportFlags & adicionalLandPointFlags) != 0)
                {
                    if (land.LandData.LandingType == (byte)LandingType.LandingPoint &&
                        land.LandData.UserLocation != Vector3.Zero )
                        // &&
                        // land.LandData.OwnerID != m_uuid )
                    {
                        pos = land.LandData.UserLocation;
                        if(land.LandData.UserLookAt != Vector3.Zero)
                            lookat = land.LandData.UserLookAt;
                        positionChanged = true;
                    }
                }
            }
            return true;
        }

        private DetectedObject CreateDetObject(SceneObjectPart obj)
        {
            DetectedObject detobj = new DetectedObject();
            detobj.keyUUID = obj.UUID;
            detobj.nameStr = obj.Name;
            detobj.ownerUUID = obj.OwnerID;
            detobj.posVector = obj.AbsolutePosition;
            detobj.rotQuat = obj.GetWorldRotation();
            detobj.velVector = obj.Velocity;
            detobj.colliderType = 0;
            detobj.groupUUID = obj.GroupID;
            detobj.linkNumber = 0;

            return detobj;
        }

        private DetectedObject CreateDetObject(ScenePresence av)
        {
            DetectedObject detobj = new DetectedObject();
            detobj.keyUUID = av.UUID;
            detobj.nameStr = av.ControllingClient.Name;
            detobj.ownerUUID = av.UUID;
            detobj.posVector = av.AbsolutePosition;
            detobj.rotQuat = av.Rotation;
            detobj.velVector = av.Velocity;
            detobj.colliderType = 0;
            detobj.groupUUID = av.ControllingClient.ActiveGroupId;
            detobj.linkNumber = 0;

            return detobj;
        }

        private DetectedObject CreateDetObjectForGround()
        {
            DetectedObject detobj = new DetectedObject();
            detobj.keyUUID = UUID.Zero;
            detobj.nameStr = "";
            detobj.ownerUUID = UUID.Zero;
            detobj.posVector = AbsolutePosition;
            detobj.rotQuat = Quaternion.Identity;
            detobj.velVector = Vector3.Zero;
            detobj.colliderType = 0;
            detobj.groupUUID = UUID.Zero;
            detobj.linkNumber = 0;
            return detobj;
        }

        private ColliderArgs CreateColliderArgs(SceneObjectPart dest, List<uint> colliders)
        {
            ColliderArgs colliderArgs = new ColliderArgs();
            List<DetectedObject> colliding = new List<DetectedObject>();
            foreach (uint localId in colliders)
            {
                if (localId == 0)
                    continue;

                SceneObjectPart obj = m_scene.GetSceneObjectPart(localId);
                if (obj != null)
                {
                    if (!dest.CollisionFilteredOut(obj.UUID, obj.Name))
                        colliding.Add(CreateDetObject(obj));
                }
                else
                {
                    ScenePresence av = m_scene.GetScenePresence(localId);
                    if (av != null && (!av.IsChildAgent))
                    {
                        if (!dest.CollisionFilteredOut(av.UUID, av.Name))
                            colliding.Add(CreateDetObject(av));
                    }
                }
            }

            colliderArgs.Colliders = colliding;

            return colliderArgs;
        }

        private delegate void ScriptCollidingNotification(uint localID, ColliderArgs message);

        private void SendCollisionEvent(SceneObjectGroup dest, scriptEvents ev, List<uint> colliders, ScriptCollidingNotification notify)
        {
            ColliderArgs CollidingMessage;

            if (colliders.Count > 0)
            {
                if ((dest.RootPart.ScriptEvents & ev) != 0)
                {
                    CollidingMessage = CreateColliderArgs(dest.RootPart, colliders);

                    if (CollidingMessage.Colliders.Count > 0)
                        notify(dest.RootPart.LocalId, CollidingMessage);
                }
            }
        }

        private void SendLandCollisionEvent(SceneObjectGroup dest, scriptEvents ev, ScriptCollidingNotification notify)
        {
            if ((dest.RootPart.ScriptEvents & ev) != 0)
            {
                ColliderArgs LandCollidingMessage = new ColliderArgs();
                List<DetectedObject> colliding = new List<DetectedObject>();

                colliding.Add(CreateDetObjectForGround());
                LandCollidingMessage.Colliders = colliding;

                notify(dest.RootPart.LocalId, LandCollidingMessage);
            }
        }

        private void RaiseCollisionScriptEvents(Dictionary<uint, ContactPoint> coldata)
        {
            try
            {
                List<uint> thisHitColliders = new List<uint>();
                List<uint> endedColliders = new List<uint>();
                List<uint> startedColliders = new List<uint>();
                List<CollisionForSoundInfo> soundinfolist = new List<CollisionForSoundInfo>();
                CollisionForSoundInfo soundinfo;
                ContactPoint curcontact;

                if (coldata.Count == 0)
                {
                    if (m_lastColliders.Count == 0)
                        return; // nothing to do

                    foreach (uint localID in m_lastColliders)
                    {
                        endedColliders.Add(localID);
                    }
                    m_lastColliders.Clear();
                }

                else
                {
                    bool candoparcelSound = ParcelAllowThisAvatarSounds;

                    foreach (uint id in coldata.Keys)
                    {
                        thisHitColliders.Add(id);
                        if (!m_lastColliders.Contains(id))
                        {
                            startedColliders.Add(id);
                            curcontact = coldata[id];
                            if (candoparcelSound && Math.Abs(curcontact.RelativeSpeed) > 0.2)
                            {
                                soundinfo = new CollisionForSoundInfo();
                                soundinfo.colliderID = id;
                                soundinfo.position = curcontact.Position;
                                soundinfo.relativeVel = curcontact.RelativeSpeed;
                                soundinfolist.Add(soundinfo);
                            }
                        }
                        //m_log.Debug("[SCENE PRESENCE]: Collided with:" + localid.ToString() + " at depth of: " + collissionswith[localid].ToString());
                    }

                    // calculate things that ended colliding
                    foreach (uint localID in m_lastColliders)
                    {
                        if (!thisHitColliders.Contains(localID))
                        {
                            endedColliders.Add(localID);
                        }
                    }
                    //add the items that started colliding this time to the last colliders list.
                    foreach (uint localID in startedColliders)
                    {
                        m_lastColliders.Add(localID);
                    }
                    // remove things that ended colliding from the last colliders list
                    foreach (uint localID in endedColliders)
                    {
                        m_lastColliders.Remove(localID);
                    }

                    if (soundinfolist.Count > 0)
                        CollisionSounds.AvatarCollisionSound(this, soundinfolist);
                }

                foreach (SceneObjectGroup att in GetAttachments())
                {
                    SendCollisionEvent(att, scriptEvents.collision_start, startedColliders, m_scene.EventManager.TriggerScriptCollidingStart);
                    SendCollisionEvent(att, scriptEvents.collision      , m_lastColliders , m_scene.EventManager.TriggerScriptColliding);
                    SendCollisionEvent(att, scriptEvents.collision_end  , endedColliders  , m_scene.EventManager.TriggerScriptCollidingEnd);

                    if (startedColliders.Contains(0))
                        SendLandCollisionEvent(att, scriptEvents.land_collision_start, m_scene.EventManager.TriggerScriptLandCollidingStart);
                    if (m_lastColliders.Contains(0))
                        SendLandCollisionEvent(att, scriptEvents.land_collision, m_scene.EventManager.TriggerScriptLandColliding);
                    if (endedColliders.Contains(0))
                        SendLandCollisionEvent(att, scriptEvents.land_collision_end, m_scene.EventManager.TriggerScriptLandCollidingEnd);
                }
            }
            catch { }
//            finally
//            {
//                m_collisionEventFlag = false;
//            }
        }

        private void TeleportFlagsDebug() {
    
            // Some temporary debugging help to show all the TeleportFlags we have...
            bool HG = false;
            if((m_teleportFlags & TeleportFlags.ViaHGLogin) == TeleportFlags.ViaHGLogin)
                HG = true;
    
            m_log.InfoFormat("[SCENE PRESENCE]: TELEPORT ******************");
    
            uint i = 0u;
            for (int x = 0; x <= 30 ; x++, i = 1u << x)
            {
                i = 1u << x;
    
                if((m_teleportFlags & (TeleportFlags)i) == (TeleportFlags)i)
                    if (HG == false)
                        m_log.InfoFormat("[SCENE PRESENCE]: Teleport Flags include {0}", ((TeleportFlags) i).ToString());
                    else
                        m_log.InfoFormat("[SCENE PRESENCE]: HG Teleport Flags include {0}", ((TeleportFlags)i).ToString());
            }
    
            m_log.InfoFormat("[SCENE PRESENCE]: TELEPORT ******************");
    
        }

        private void parcelGodCheck(UUID currentParcelID, bool isGod)
        {
            List<ScenePresence> allpresences = m_scene.GetScenePresences();

            foreach (ScenePresence p in allpresences)
            {
                if (p.IsDeleted || p.IsChildAgent || p == this || p.ControllingClient == null || !p.ControllingClient.IsActive)
                    continue;

                if (p.ParcelHideThisAvatar && p.currentParcelUUID != currentParcelID)
                {
                    if (isGod)
                        p.SendViewTo(this);
                    else
                        p.SendKillTo(this);
                }
            }
        }

        private void ParcelCrossCheck(UUID currentParcelID,UUID previusParcelID,
                            bool currentParcelHide, bool previusParcelHide, bool oldhide,bool check)
        {
            List<ScenePresence> killsToSendto = new List<ScenePresence>();
            List<ScenePresence> killsToSendme = new List<ScenePresence>();
            List<ScenePresence> viewsToSendto = new List<ScenePresence>();
            List<ScenePresence> viewsToSendme = new List<ScenePresence>();
            List<ScenePresence> allpresences = null;

            if (IsInTransit || IsChildAgent)
                return;

            if (check)
            {
                // check is relative to current parcel only
                if (currentParcelUUID == null || oldhide == currentParcelHide)
                    return;

                allpresences = m_scene.GetScenePresences();

                if (oldhide)
                { // where private
                    foreach (ScenePresence p in allpresences)
                    {
                        if (p.IsDeleted || p == this || p.ControllingClient == null || !p.ControllingClient.IsActive)
                            continue;

                        // those on not on parcel see me
                        if (currentParcelID != p.currentParcelUUID)
                        {                         
                            viewsToSendto.Add(p); // they see me
                        }
                    }
                } // where private end

                else
                { // where public
                    foreach (ScenePresence p in allpresences)
                    {
                        if (p.IsDeleted || p == this || p.ControllingClient == null || !p.ControllingClient.IsActive)
                            continue;

                        // those not on parcel dont see me
                        if (currentParcelID != p.currentParcelUUID && p.GodLevel < 200)
                        {
                            killsToSendto.Add(p); // they dont see me
                        }
                    }
                } // where public end

                allpresences.Clear();
            }
            else
            {
                if (currentParcelHide)
                {
                    // now on a private parcel
                    allpresences = m_scene.GetScenePresences();

                    if (previusParcelHide && previusParcelID != UUID.Zero)
                    {
                        foreach (ScenePresence p in allpresences)
                        {
                            if (p.IsDeleted || p == this || p.ControllingClient == null || !p.ControllingClient.IsActive)
                                continue;

                            // only those on previus parcel need receive kills
                            if (previusParcelID == p.currentParcelUUID)
                            {
                                if(p.GodLevel < 200)
                                    killsToSendto.Add(p); // they dont see me
                                if(GodLevel < 200)
                                    killsToSendme.Add(p);  // i dont see them
                            }
                            // only those on new parcel need see
                            if (currentParcelID == p.currentParcelUUID)
                            {
                                viewsToSendto.Add(p); // they see me
                                viewsToSendme.Add(p); // i see them
                            }
                        }
                    }
                    else
                    {
                        //was on a public area
                        allpresences = m_scene.GetScenePresences();

                        foreach (ScenePresence p in allpresences)
                        {
                            if (p.IsDeleted || p == this || p.ControllingClient == null || !p.ControllingClient.IsActive)
                                continue;

                            // those not on new parcel dont see me
                            if (currentParcelID != p.currentParcelUUID && p.GodLevel < 200)
                            {
                                killsToSendto.Add(p); // they dont see me
                            }
                            else
                            {
                                viewsToSendme.Add(p); // i see those on it
                            }
                        }
                    }
                    allpresences.Clear();
                } // now on a private parcel end 

                else
                {
                    // now on public parcel
                    if (previusParcelHide && previusParcelID != UUID.Zero)
                    {
                        // was on private area
                        allpresences = m_scene.GetScenePresences();

                        foreach (ScenePresence p in allpresences)
                        {
                            if (p.IsDeleted || p == this || p.ControllingClient == null || !p.ControllingClient.IsActive)
                                continue;
                            // only those old parcel need kills
                            if (previusParcelID == p.currentParcelUUID && GodLevel < 200)
                            {
                                killsToSendme.Add(p);  // i dont see them
                            }
                            else
                            {
                                viewsToSendto.Add(p); // they see me
                            }
                        }
                    }
                    else
                        return; // was on a public area also
                } // now on public parcel end
            }

            // send the things

            if (killsToSendto.Count > 0)
            {
                foreach (ScenePresence p in killsToSendto)
                {
//                    m_log.Debug("[AVATAR]: killTo: " + Lastname + " " + p.Lastname);
                    SendKillTo(p);
                }
            }

            if (killsToSendme.Count > 0)
            {
                foreach (ScenePresence p in killsToSendme)
                {
//                    m_log.Debug("[AVATAR]: killToMe: " + Lastname + " " + p.Lastname);
                    p.SendKillTo(this);
                }
            }

            if (viewsToSendto.Count > 0)
            {
                foreach (ScenePresence p in viewsToSendto)
                {
                    SendViewTo(p);
                }
            }

            if (viewsToSendme.Count > 0 )
            {
                foreach (ScenePresence p in viewsToSendme)
                {
                    if (p.IsChildAgent)
                        continue;
//                   m_log.Debug("[AVATAR]: viewMe: " + Lastname + "<-" + p.Lastname);
                    p.SendViewTo(this);
                }
            }
        }

        public void HasMovedAway(bool nearRegion)
        {

            if (nearRegion)
            {
                if (Scene.AttachmentsModule != null)
                    Scene.AttachmentsModule.DeleteAttachmentsFromScene(this, true);

                if (!ParcelHideThisAvatar || GodLevel >= 200)
                    return;

                List<ScenePresence> allpresences = m_scene.GetScenePresences();
                foreach (ScenePresence p in allpresences)
                {
                    if (p.IsDeleted || p == this || p.IsChildAgent || p.ControllingClient == null || !p.ControllingClient.IsActive)
                        continue;

                    if (p.currentParcelUUID == m_currentParcelUUID)
                    {
                        p.SendKillTo(this);
                    }
                }
            }
            else
            {
                List<ScenePresence> allpresences = m_scene.GetScenePresences();
                foreach (ScenePresence p in allpresences)
                {
                    if (p == this)
                        continue;
                    SendKillTo(p);
                    if (!p.IsChildAgent)
                        p.SendKillTo(this);
                }

                if (Scene.AttachmentsModule != null)
                    Scene.AttachmentsModule.DeleteAttachmentsFromScene(this, true);
            }
        }


//  kill with attachs root kills
        public void SendKillTo(ScenePresence p)
        {
            List<uint> ids = new List<uint>(m_attachments.Count + 1);
            foreach (SceneObjectGroup sog in m_attachments)
            {
                ids.Add(sog.RootPart.LocalId);
            }

            ids.Add(LocalId);
            p.ControllingClient.SendKillObject(ids);
        }

/*
// kill with hack
        public void SendKillTo(ScenePresence p)
        {
            foreach (SceneObjectGroup sog in m_attachments)
                p.ControllingClient.SendPartFullUpdate(sog.RootPart, LocalId + 1);
            p.ControllingClient.SendKillObject(new List<uint> { LocalId });
        }
*/
        public void SendViewTo(ScenePresence p)
        {
            SendAvatarDataToAgentNF(p);
            SendAppearanceToAgent(p);
            if (Animator != null)
                Animator.SendAnimPackToClient(p.ControllingClient);
            SendAttachmentsToAgentNF(p);
        }

        public void SetAnimationOverride(string animState, UUID animID)
        {
            Overrides.SetOverride(animState, animID);
//            Animator.SendAnimPack();
            Animator.ForceUpdateMovementAnimations();
        }

        public UUID GetAnimationOverride(string animState)
        {
            return Overrides.GetOverriddenAnimation(animState);
        }
    }
}
