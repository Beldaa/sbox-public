namespace Sandbox;

/// <summary>
/// A rotating door that can be opened by players pressing USE (E).
/// Supports locking, auto-close delay and open direction control.
/// Compatible with prop_door_rotating map entities.
/// </summary>
[Expose]
[Title( "Door Rotating" )]
[Category( "Game" )]
[Icon( "door_front" )]
public sealed class DoorRotating : Component, Component.IPressable, Component.IDamageable
{
	/// <summary>
	/// The number of degrees the door rotates when opened.
	/// </summary>
	[Property] public float RotationDistance { get; set; } = 90f;

	/// <summary>
	/// Default rotation speed in degrees per second. Used as the property
	/// default and as the fallback value when MapInstance loads a
	/// prop_door_rotating without an explicit "speed" keyvalue.
	/// </summary>
	public const float DefaultSpeed = 300f;

	/// <summary>
	/// Speed at which the door rotates, in degrees per second.
	/// </summary>
	[Property] public float Speed { get; set; } = DefaultSpeed;

	/// <summary>
	/// Health of the door. 0 means unbreakable.
	/// </summary>
	[Property, Sync] public float Health { get; set; }

	/// <summary>
	/// Seconds before the door auto-closes. -1 means never.
	/// </summary>
	[Property] public float AutoCloseDelay { get; set; } = -1f;

	/// <summary>
	/// Is the door currently locked?
	/// </summary>
	[Property, Sync] public bool IsLocked { get; set; }

	/// <summary>
	/// Controls which direction the door is allowed to open.
	/// </summary>
	public enum OpenDirectionMode
	{
		Both,
		ForwardOnly,
		BackwardOnly
	}

	/// <summary>
	/// Which direction can the door open.
	/// </summary>
	[Property] public OpenDirectionMode OpenDirection { get; set; } = OpenDirectionMode.Both;

	/// <summary>
	/// Local-space offset from the entity origin to the hinge/pivot point.
	/// If the model origin is already at the hinge (like Source door models), leave this at zero.
	/// Otherwise, set this to the hinge position relative to the model origin.
	/// </summary>
	[Property] public Vector3 HingeOffset { get; set; }

	/// <summary>
	/// If set, doors with the same link name operate together as one unit (double doors).
	/// All linked doors open, close, lock and unlock as a group. Set automatically from
	/// the map targetname when loading a prop_door_rotating.
	/// </summary>
	[Property, Sync] public string LinkName { get; set; }

	/// <summary>
	/// Called when the door starts opening.
	/// </summary>
	[Property, Group( "Events" )] public Action OnOpening { get; set; }

	/// <summary>
	/// Called when the door is fully open.
	/// </summary>
	[Property, Group( "Events" )] public Action OnFullyOpen { get; set; }

	/// <summary>
	/// Called when the door starts closing.
	/// </summary>
	[Property, Group( "Events" )] public Action OnClosing { get; set; }

	/// <summary>
	/// Called when the door is fully closed.
	/// </summary>
	[Property, Group( "Events" )] public Action OnFullyClosed { get; set; }

	/// <summary>
	/// Called when a locked door is used.
	/// </summary>
	[Property, Group( "Events" )] public Action OnLockedUse { get; set; }

	public enum DoorState
	{
		Closed,
		Opening,
		Open,
		Closing
	}

	[Sync] DoorState _state { get; set; } = DoorState.Closed;
	DoorState _lastObservedState = DoorState.Closed;
	TimeSince TimeSinceStateChange { get; set; }

	/// <summary>
	/// 1 = forward, -1 = backward
	/// </summary>
	[Sync] int _openSign { get; set; } = 1;

	Rotation _closedRotation;
	Vector3 _closedPosition;
	Vector3 _hingeWorldOffset;       // _closedRotation * HingeOffset, cached
	Vector3 _offsetFromHinge;        // -_hingeWorldOffset, cached
	Collider _collider;              // cached for pushaway queries
	readonly HashSet<GameObject> _pushedPlayers = new();
	readonly HashSet<PhysicsBody> _pushedBodies = new();
	// Reused per fixed-tick to share a single FindInPhysics query between
	// the blocking check and the pushaway pass.
	readonly List<GameObject> _scanBuffer = new();

	/// <summary>
	/// The current state of the door.
	/// </summary>
	public DoorState State
	{
		get => _state;
		private set
		{
			if ( _state == value )
				return;

			_state = value;

			// Fire side-effects directly here on whoever changed the state
			// (typically the host). PollStateChange picks up incoming [Sync]
			// updates on the other peers.
			_lastObservedState = value;
			TimeSinceStateChange = 0;
			OnDoorStateChanged( value );
		}
	}

	/// <summary>
	/// Checks for state changes every frame and fires side-effect events.
	/// Works on host and clients alike because _state is [Sync]ed. Avoids the
	/// [Change] callback which can NRE during early network sync before the
	/// component is fully initialized.
	/// </summary>
	void PollStateChange()
	{
		if ( _state == _lastObservedState )
			return;

		var newState = _state;
		_lastObservedState = newState;
		TimeSinceStateChange = 0;
		OnDoorStateChanged( newState );
	}

	protected override void OnStart()
	{
		_closedRotation = Transform.Local.Rotation;
		_closedPosition = Transform.Local.Position;
		_hingeWorldOffset = _closedRotation * HingeOffset;
		_offsetFromHinge = -_hingeWorldOffset;
		_collider = GetComponent<Collider>();
		_lastObservedState = _state;
	}

	public void OnDamage( in DamageInfo damage )
	{
		if ( IsProxy ) return;
		if ( Health <= 0f ) return; // 0 = unbreakable

		Health -= damage.Damage;

		if ( Health <= 0f )
		{
			Health = 0f;
			GameObject.Destroy();
		}
	}

	void OnDoorStateChanged( DoorState state )
	{
		switch ( state )
		{
			case DoorState.Opening: OnOpening?.Invoke(); break;
			case DoorState.Open: OnFullyOpen?.Invoke(); break;
			case DoorState.Closing: OnClosing?.Invoke(); break;
			case DoorState.Closed: OnFullyClosed?.Invoke(); break;
		}
	}

	/// <summary>
	/// Yields all other doors that share this door's <see cref="LinkName"/>.
	/// Used to operate double doors as a single unit.
	/// </summary>
	IEnumerable<DoorRotating> LinkedDoors()
	{
		if ( string.IsNullOrEmpty( LinkName ) ) yield break;

		foreach ( var other in Scene.GetAllComponents<DoorRotating>() )
		{
			if ( other == this ) continue;
			if ( other.LinkName == LinkName )
				yield return other;
		}
	}

	public bool CanPress( IPressable.Event e )
	{
		return true;
	}

	public IPressable.Tooltip? GetTooltip( IPressable.Event e )
	{
		if ( IsLocked )
		{
			return new IPressable.Tooltip( "Locked", "lock", "This door is locked." );
		}

		var action = State switch
		{
			DoorState.Closed => "Open",
			DoorState.Open => "Close",
			DoorState.Opening => "Close",
			DoorState.Closing => "Open",
			_ => null
		};

		if ( action is null )
			return null;

		return new IPressable.Tooltip( action, "door_front", $"{action} door" );
	}

	public bool Press( IPressable.Event e )
	{
		if ( IsLocked )
		{
			OnLockedUse?.Invoke();
			foreach ( var sibling in LinkedDoors() )
				sibling.OnLockedUse?.Invoke();
			return true; // consumed — don't let the press fall through to anything behind us
		}

		var sign = ComputeOpenSign( e );

		ApplyPress( sign );
		foreach ( var sibling in LinkedDoors() )
			sibling.ApplyPress( sign );

		return true;
	}

	void ApplyPress( int sign )
	{
		if ( State == DoorState.Closed )
			OpenInDirection( sign );
		else if ( State == DoorState.Open )
			Close();
		else if ( State == DoorState.Opening )
			Close();
		else if ( State == DoorState.Closing )
			ReverseToOpening();
	}

	/// <summary>
	/// Decide which direction (±1) the door should swing relative to its closed orientation.
	/// Uses the closed-state forward axis (not the current one) so the calculation is stable
	/// while the door is mid-rotation or already open. Assumes the door is a root object
	/// (i.e. WorldRotation == Transform.Local.Rotation), which is true for map-loaded doors.
	/// </summary>
	int ComputeOpenSign( IPressable.Event e )
	{
		if ( OpenDirection == OpenDirectionMode.ForwardOnly ) return 1;
		if ( OpenDirection == OpenDirectionMode.BackwardOnly ) return -1;

		if ( !e.Source.IsValid() ) return _openSign;

		var closedForward = _closedRotation.Forward;
		var toPlayer = (e.Source.WorldPosition - WorldPosition).Normal;
		return Vector3.Dot( closedForward, toPlayer ) >= 0 ? -1 : 1; // away from player
	}

	/// <summary>
	/// Opens the door using its current <see cref="_openSign"/>.
	/// </summary>
	[Rpc.Host]
	public void Open()
	{
		OpenInDirection( _openSign );
	}

	/// <summary>
	/// Opens the door in the given direction (+1 forward, -1 backward).
	/// If the requested side is blocked by a non-pushable body and OpenDirection
	/// is Both, automatically tries the opposite side instead. Gives up only if
	/// both sides are blocked. Direction is set on the host so all clients see
	/// the same swing.
	/// </summary>
	[Rpc.Host]
	public void OpenInDirection( int sign )
	{
		if ( IsLocked || State is DoorState.Open or DoorState.Opening )
			return;

		// If we're mid-closing, just revert direction — the existing _openSign
		// is the only safe choice (re-evaluating blockers here would be wrong
		// because IsBlockedByStaticBody inverts its check while State == Closing).
		if ( State == DoorState.Closing )
		{
			State = DoorState.Opening;
			return;
		}

		sign = sign >= 0 ? 1 : -1;

		// If the preferred side is blocked, fall back to the opposite side
		// (only when the designer hasn't pinned the swing direction).
		if ( OpenDirection == OpenDirectionMode.Both && IsBlockedByStaticBody( sign ) )
		{
			if ( !IsBlockedByStaticBody( -sign ) )
				sign = -sign;
			else
				return; // both sides blocked — give up
		}

		_openSign = sign;
		State = DoorState.Opening;
	}

	/// <summary>
	/// Closes the door.
	/// </summary>
	[Rpc.Host]
	public void Close()
	{
		if ( IsLocked || State is DoorState.Closed or DoorState.Closing )
			return;

		State = DoorState.Closing;
	}

	/// <summary>
	/// Reverts a Closing door back to Opening, preserving the current open
	/// direction. Unlike OpenInDirection, this does NOT auto-flip the swing
	/// side based on blockers — flipping mid-swing would make the door pass
	/// through obstacles that stopped its return path.
	/// </summary>
	[Rpc.Host]
	public void ReverseToOpening()
	{
		if ( IsLocked ) return;
		if ( State != DoorState.Closing ) return;

		State = DoorState.Opening;
	}

	/// <summary>
	/// Toggles the door between open and closed using the current <see cref="_openSign"/>.
	/// Mid-swing presses reverse the current motion.
	/// </summary>
	[Rpc.Host]
	public void Toggle()
	{
		switch ( State )
		{
			case DoorState.Closed: OpenInDirection( _openSign ); break;
			case DoorState.Open: Close(); break;
			case DoorState.Opening: Close(); break;
			case DoorState.Closing: ReverseToOpening(); break;
		}
	}

	/// <summary>
	/// Locks the door (and any linked doors).
	/// </summary>
	[Rpc.Host]
	public void Lock()
	{
		IsLocked = true;
		foreach ( var sibling in LinkedDoors() )
			sibling.IsLocked = true;
	}

	/// <summary>
	/// Unlocks the door (and any linked doors).
	/// </summary>
	[Rpc.Host]
	public void Unlock()
	{
		IsLocked = false;
		foreach ( var sibling in LinkedDoors() )
			sibling.IsLocked = false;
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy )
			return;

		if ( State is DoorState.Opening or DoorState.Closing )
		{
			if ( IsBlockedByStaticBody( _openSign ) )
				return;

			var prev = WorldTransform;
			UpdateRotation( State == DoorState.Closing ? 0 : _openSign );
			PushawayNearby( prev );
		}
		else if ( State == DoorState.Open && AutoCloseDelay >= 0 && TimeSinceStateChange >= AutoCloseDelay )
		{
			Close();
		}
	}

	/// <summary>
	/// Blocking threshold: the minimum surface-to-surface distance below which
	/// a non-pushable body is considered in the door's path.
	/// Needs to be generous enough to catch objects before visual intersection.
	/// </summary>
	const float BlockDistance = 4f;

	/// <summary>
	/// Padding added to the hinge search radius so we still find bodies that
	/// extend slightly beyond the door's bounding swing arc.
	/// </summary>
	const float SearchRadiusPad = 16f;

	/// <summary>
	/// Maximum distance from the door surface at which a player receives a push.
	/// </summary>
	const float PlayerPushReach = 24f;

	/// <summary>
	/// Maximum distance from the door surface at which a physics body receives a push.
	/// </summary>
	const float BodyPushReach = 16f;

	/// <summary>
	/// Returns true if a non-pushable physics body is in the door's path AND the
	/// door's current rotational motion would push the door surface INTO it.
	/// This directional test means the door can always reverse direction even if
	/// it's currently in contact with a prop — only motion that decreases distance
	/// blocks. Set <paramref name="sign"/> to the desired open direction (±1);
	/// the test inverts automatically while State is Closing.
	/// </summary>
	bool IsBlockedByStaticBody( int sign )
	{
		if ( _collider is null )
			return false;

		var localBounds = _collider.LocalBounds;
		var doorReach = MathF.Max( localBounds.Size.x, localBounds.Size.y ) + localBounds.Center.Length;
		var hingeWorld = WorldPosition + WorldRotation * HingeOffset;

		// Effective rotation sign: while opening we rotate by +sign; while closing
		// we return toward 0 from sign*RotationDistance, so motion is in -sign.
		int effectiveSign = State == DoorState.Closing ? -sign : sign;

		// Single nearby scan; reused by PushawayNearby on this same tick.
		_scanBuffer.Clear();
		foreach ( var go in Scene.FindInPhysics( new Sphere( hingeWorld, doorReach + SearchRadiusPad ) ) )
		{
			if ( !go.IsValid() || go == GameObject || go.IsDescendant( GameObject ) )
				continue;
			_scanBuffer.Add( go );
		}

		foreach ( var go in _scanBuffer )
		{
			// Only check objects with Rigidbody — this excludes map geometry
			// (floors, walls, door frames) which would otherwise always block.
			var rb = go.GetComponentInParent<Rigidbody>();
			if ( !rb.IsValid() ) continue;

			var body = rb.PhysicsBody;
			if ( !body.IsValid() ) continue;

			// Movable dynamic props get pushed away — they don't stop the door.
			bool isMovableDynamic = body.BodyType == PhysicsBodyType.Dynamic && body.MotionEnabled;
			if ( isMovableDynamic ) continue;

			var col = go.GetComponentInChildren<Collider>();
			if ( !col.IsValid() || col.IsTrigger ) continue;

			// Closest-point pair between door and prop. Refine once for robustness.
			var pOnDoor = _collider.FindClosestPoint( body.MassCenter );
			var pOnProp = col.FindClosestPoint( pOnDoor );
			pOnDoor = _collider.FindClosestPoint( pOnProp );
			var toward = pOnProp - pOnDoor;
			var distNow = toward.Length;
			if ( distNow >= BlockDistance ) continue;

			// If we're overlapping, the closest-point delta degenerates to ~0 and
			// can't tell us which side of the door the prop sits on. Fall back to
			// the prop's mass center relative to the contact point so we can still
			// resolve a direction.
			if ( distNow < 0.0001f )
			{
				toward = (body.MassCenter - pOnDoor).WithZ( 0 );
				if ( toward.LengthSquared < 0.0001f )
					continue;
			}

			// Tangential velocity at the contact point: ω × r, with ω along world
			// up scaled by effectiveSign. We only need its direction.
			var radial = (pOnDoor - hingeWorld).WithZ( 0 );
			if ( radial.LengthSquared < 0.0001f ) continue;
			var velDir = Vector3.Cross( Vector3.Up * effectiveSign, radial );

			// If door surface is moving toward the prop, this contact blocks us.
			if ( Vector3.Dot( velDir, toward ) > 0f )
				return true;
		}

		return false;
	}

	void UpdateRotation( int sign )
	{
		// Yaw relative to the door's closed orientation (0 = closed, ±RotationDistance = fully open)
		var targetYaw = State == DoorState.Closing ? 0f : RotationDistance * sign;
		var deltaRot = Rotation.FromYaw( targetYaw );

		// Compute target rotation
		var targetRotation = _closedRotation * deltaRot;

		// Orbit around the hinge point (cached vectors computed once in OnStart)
		var hingeLocal = _closedPosition + _hingeWorldOffset;
		var targetPosition = hingeLocal + deltaRot * _offsetFromHinge;

		var currentRotation = Transform.Local.Rotation;
		var currentPosition = Transform.Local.Position;
		var step = Speed * Time.Delta;

		// Get the angular difference
		var diff = Rotation.Difference( currentRotation, targetRotation );
		var angle = diff.Angle();

		if ( angle <= step || angle < 0.1f )
		{
			// Snap to target
			Transform.Local = Transform.Local.WithPosition( targetPosition ).WithRotation( targetRotation );

			if ( State == DoorState.Opening )
				State = DoorState.Open;
			else if ( State == DoorState.Closing )
				State = DoorState.Closed;
		}
		else
		{
			// Interpolate toward target
			var t = step / angle;
			Transform.Local = Transform.Local
				.WithPosition( Vector3.Lerp( currentPosition, targetPosition, t ) )
				.WithRotation( Rotation.Slerp( currentRotation, targetRotation, t ) );
		}
	}

	/// <summary>
	/// Source Engine 1 style pushaway. Computes the door's angular velocity around the
	/// hinge axis, then for each nearby entity calculates the tangential velocity at
	/// their radial distance from the hinge. Only pushes entities that are within the
	/// door's sweep arc and reach. Reuses the candidate list filled by the most recent
	/// <see cref="IsBlockedByStaticBody"/> call this tick.
	/// </summary>
	void PushawayNearby( Transform prev )
	{
		if ( _collider is null )
			return;

		var current = WorldTransform;
		if ( prev.Position.AlmostEqual( current.Position ) && prev.Rotation == current.Rotation )
			return;

		var hingeWorld = WorldPosition + WorldRotation * HingeOffset;

		// Compute angular velocity (degrees this frame → radians/sec).
		var deltaRot = Rotation.Difference( prev.Rotation, current.Rotation );
		var angDeg = deltaRot.Angle();
		if ( angDeg < 0.01f )
			return;

		// Sign of the rotation around world up. Using the rotation axis avoids the
		// wrap-around issue you'd get from subtracting yaws (e.g. 179° → -179°).
		var rotationAxis = new Vector3( deltaRot.x, deltaRot.y, deltaRot.z );
		var angularSign = Vector3.Dot( rotationAxis, Vector3.Up ) >= 0 ? 1f : -1f;
		var angularSpeed = angDeg * MathF.PI / 180f / Time.Delta; // rad/s

		// Door reach from bounds.
		var localBounds = _collider.LocalBounds;
		var doorReach = MathF.Max( localBounds.Size.x, localBounds.Size.y ) + localBounds.Center.Length;

		_pushedPlayers.Clear();
		_pushedBodies.Clear();

		foreach ( var go in _scanBuffer )
		{
			if ( !go.IsValid() )
				continue;

			// --- Player ---
			var player = go.GetComponentInParent<PlayerController>();
			if ( player.IsValid() && player.Body.IsValid() && _pushedPlayers.Add( player.GameObject ) )
			{
				PushPlayerSourceStyle( player, hingeWorld, doorReach, angularSpeed, angularSign );
				continue;
			}

			// --- Rigidbody props ---
			foreach ( var rb in go.GetComponentsInChildren<Rigidbody>() )
			{
				if ( !rb.IsValid() ) continue;
				var body = rb.PhysicsBody;
				if ( !body.IsValid() || body.BodyType != PhysicsBodyType.Dynamic ) continue;
				if ( !_pushedBodies.Add( body ) ) continue;
				PushBodySourceStyle( body, hingeWorld, doorReach, angularSpeed, angularSign );
			}
		}
	}

	/// <summary>
	/// Computes the tangential push direction and speed for a point at a given
	/// radial offset from the hinge. Returns false if the point is outside the
	/// door's reach or the tangential speed is negligible.
	/// </summary>
	bool ComputeTangentialPush( Vector3 entityPos, Vector3 hingeWorld, float doorReach,
		float angularSpeed, float angularSign, out Vector3 pushDir, out float tangentSpeed )
	{
		pushDir = default;
		tangentSpeed = 0f;

		// Work in 2D (XY plane) relative to the hinge.
		var offset = entityPos - hingeWorld;
		var flat = offset.WithZ( 0 );
		var radius = flat.Length;

		// Must be within the door's sweep radius (with a small margin for the player body).
		if ( radius < 1f || radius > doorReach + SearchRadiusPad )
			return false;

		// Tangential direction: perpendicular to the radius in the rotation direction.
		// For positive yaw (counter-clockwise from above): tangent = (-y, x, 0).Normal
		// For negative yaw (clockwise): tangent = (y, -x, 0).Normal
		var radialDir = flat.Normal;
		pushDir = angularSign >= 0
			? new Vector3( -radialDir.y, radialDir.x, 0 )
			: new Vector3( radialDir.y, -radialDir.x, 0 );

		tangentSpeed = angularSpeed * radius;
		return tangentSpeed > 1f;
	}

	void PushPlayerSourceStyle( PlayerController player, Vector3 hingeWorld,
		float doorReach, float angularSpeed, float angularSign )
	{
		var playerPos = player.WorldPosition + Vector3.Up * player.CurrentHeight * 0.5f;

		if ( !ComputeTangentialPush( playerPos, hingeWorld, doorReach, angularSpeed, angularSign,
			out var pushDir, out var tangentSpeed ) )
			return;

		// Also verify with FindClosestPoint — only push if the door surface is nearby.
		var closest = _collider.FindClosestPoint( playerPos );
		var distToDoor = (playerPos - closest).Length;
		if ( distToDoor > PlayerPushReach )
			return;

		// Push the player along the tangent, scaled by proximity (closer = stronger).
		// `push` is already a velocity (units/s), so add it to Velocity directly —
		// scaling by Time.Delta would turn it into a per-frame displacement and
		// only contribute ~1/64 of the intended push.
		var proximityScale = 1f - MathF.Min( distToDoor / PlayerPushReach, 1f );
		var push = pushDir * tangentSpeed * proximityScale;
		player.Body.Velocity += push.WithZ( 0 );
	}

	void PushBodySourceStyle( PhysicsBody body, Vector3 hingeWorld,
		float doorReach, float angularSpeed, float angularSign )
	{
		var bodyPos = body.MassCenter;

		if ( !ComputeTangentialPush( bodyPos, hingeWorld, doorReach, angularSpeed, angularSign,
			out var pushDir, out var tangentSpeed ) )
			return;

		// Verify the door surface is actually near this body.
		var closest = _collider.FindClosestPoint( bodyPos );
		var distToDoor = (bodyPos - closest).Length;
		if ( distToDoor > BodyPushReach )
			return;

		body.Sleeping = false;

		// Only add the difference between the tangential speed and the body's current
		// velocity along the push direction, so we don't stack impulses.
		var currentAlong = Vector3.Dot( body.GetVelocityAtPoint( bodyPos ), pushDir );
		var delta = tangentSpeed - currentAlong;
		if ( delta <= 0f )
			return;

		var proximityScale = 1f - MathF.Min( distToDoor / BodyPushReach, 1f );
		body.ApplyImpulseAt( closest, pushDir * delta * body.Mass * proximityScale );
	}

	protected override void OnUpdate()
	{
		// Detect synced state changes (runs on host + all clients).
		PollStateChange();
	}
}
