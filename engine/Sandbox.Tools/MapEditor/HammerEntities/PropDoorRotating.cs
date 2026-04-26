namespace Editor.MapEditor.EntityDefinitions;

/// <summary>
/// An entity used to place a door in the world. If two doors have the same name, they will open and close together as double doors.
/// The door rotates around its model origin, so the model origin should be placed at the hinge location.
/// </summary>
[HammerEntity]
[Library( "prop_door_rotating" )]
[Model( Archetypes = ModelArchetype.animated_model ), RenderFields, VisGroup( VisGroup.Dynamic )]
[Title( "Door Rotating" ), Category( "Gameplay" ), Icon( "door_front" )]
class PropDoorRotating : HammerEntityDefinition
{
	/// <summary>
	/// Number of degrees that the door should rotate when opened, both forward and backward.
	/// </summary>
	[Property( "distance" ), DefaultValue( 90f )]
	public float RotationDistance { get; set; } = 90f;

	/// <summary>
	/// Speed at which the door rotates, in degrees per second.
	/// </summary>
	[Property( "speed" ), DefaultValue( 200 )]
	public int Speed { get; set; } = 200;

	/// <summary>
	/// Number of seconds the door waits before closing by itself. -1 means never auto-close.
	/// </summary>
	[Property( "returndelay" ), DefaultValue( -1 )]
	public int ReturnDelay { get; set; } = -1;

	/// <summary>
	/// If set, the door starts locked and must be unlocked before it can be opened.
	/// </summary>
	[Property( "startslocked" )]
	public bool StartsLocked { get; set; } = false;

	/// <summary>
	/// Health of the door. 0 means unbreakable.
	/// </summary>
	[Property( "health" ), DefaultValue( 0f )]
	public float Health { get; set; } = 0f;

	public enum OpenDirectionType
	{
		[Title( "Open Both Directions" )]
		Both = 0,
		[Title( "Open Forward Only" )]
		ForwardOnly = 1,
		[Title( "Open Backward Only" )]
		BackwardOnly = 2
	}

	/// <summary>
	/// Force the door to open only forwards or only backwards. Normally tries to swing away from the entity that opened it.
	/// </summary>
	[Property( "opendir" ), DefaultValue( OpenDirectionType.Both )]
	public OpenDirectionType OpenDirection { get; set; } = OpenDirectionType.Both;

	[Property( "rendercolor", Title = "Color (R G B A)" )]
	[Category( "Rendering" )]
	[DefaultValue( "255 255 255 255" )]
	public Color RenderColor { get; set; }

	/// <summary>
	/// Some models have multiple versions of their textures, called skins.
	/// </summary>
	[Property( "skin", Title = "Skin" )]
	[Category( "Rendering" )]
	public string Skin { get; set; }

	[Property( "bodygroups", Title = "Body Groups" )]
	[Category( "Rendering" )]
	public string BodyGroups { get; set; }
}
