import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { GDScriptExtractor } from '../../extractors/gdscript-extractor.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('GDScriptExtractor', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    // Initialize logger for tests
    const { initializeLogger } = await import('../../utils/logger.js');
    const { MillerPaths } = await import('../../utils/miller-paths.js');
    const paths = new MillerPaths(process.cwd());
    await paths.ensureDirectories();
    initializeLogger(paths);

    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Classes and Inheritance', () => {
    it('should extract class definitions, inheritance, and built-in node types', async () => {
      const gdCode = `
# Basic class definition
class_name Player
extends CharacterBody2D

# Class with inheritance from custom class
class_name Enemy
extends Actor

# Inner class definition
class HealthComponent:
	var max_health: int = 100
	var current_health: int

	func _init(health: int = 100):
		max_health = health
		current_health = health

	func take_damage(amount: int) -> bool:
		current_health -= amount
		return current_health <= 0

	func heal(amount: int):
		current_health = min(current_health + amount, max_health)

# Class with tool annotation
@tool
class_name CustomResource
extends Resource

# Class variables and properties
var health: int = 100
var mana: float = 50.0
var player_name: String = "Unknown"
var position: Vector2
var velocity: Vector3 = Vector3.ZERO

# Export variables (GDScript 4.0+)
@export var speed: float = 200.0
@export var jump_force: float = 400.0
@export var texture: Texture2D
@export_range(0, 100) var armor: int = 10
@export_flags("Fire", "Water", "Earth", "Air") var elements: int

# Legacy export syntax (GDScript 3.x)
export var legacy_speed: float = 150.0
export(int, 0, 100) var legacy_armor: int = 5
export(PackedScene) var bullet_scene: PackedScene

# OnReady variables
@onready var sprite: Sprite2D = $Sprite2D
@onready var collision: CollisionShape2D = $CollisionShape2D
@onready var animation_player: AnimationPlayer = get_node("AnimationPlayer")

# Constants and enums
const MAX_LIVES: int = 3
const GRAVITY: float = 980.0

enum State {
	IDLE,
	WALKING,
	JUMPING,
	FALLING,
	ATTACKING
}

enum Direction { LEFT = -1, RIGHT = 1 }

# Class with multiple inheritance indicators
class_name NetworkPlayer
extends Player

# Static variables
static var instance_count: int = 0
static var global_settings: Dictionary = {}

# Setget properties (GDScript 3.x style)
var _score: int = 0 setget set_score, get_score

func set_score(value: int):
	_score = max(0, value)
	_update_ui()

func get_score() -> int:
	return _score

# Modern property syntax (GDScript 4.0+)
var level: int = 1:
	set(value):
		level = clamp(value, 1, 100)
		level_changed.emit(level)
	get:
		return level

var experience: float:
	set(value):
		experience = max(0.0, value)
		if experience >= experience_to_next_level:
			level_up()
	get:
		return experience
`;

      const result = await parserManager.parseFile('test.gd', gdCode);
      const extractor = new GDScriptExtractor('gdscript', 'test.gd', gdCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Class definitions
      const player = symbols.find(s => s.name === 'Player');
      expect(player).toBeDefined();
      expect(player?.kind).toBe(SymbolKind.Class);
      expect(player?.signature).toContain('class_name Player');
      expect(player?.baseClass).toBe('CharacterBody2D');

      const enemy = symbols.find(s => s.name === 'Enemy');
      expect(enemy).toBeDefined();
      expect(enemy?.baseClass).toBe('Actor');

      // Inner class
      const healthComponent = symbols.find(s => s.name === 'HealthComponent');
      expect(healthComponent).toBeDefined();
      expect(healthComponent?.kind).toBe(SymbolKind.Class);

      // Inner class methods
      const takeDamage = symbols.find(s => s.name === 'take_damage' && s.parentId === healthComponent?.id);
      expect(takeDamage).toBeDefined();
      expect(takeDamage?.kind).toBe(SymbolKind.Method);
      expect(takeDamage?.signature).toContain('func take_damage(amount: int) -> bool');

      // Tool class
      const customResource = symbols.find(s => s.name === 'CustomResource');
      expect(customResource).toBeDefined();
      expect(customResource?.signature).toContain('@tool');

      // Class variables
      const health = symbols.find(s => s.name === 'health');
      expect(health).toBeDefined();
      expect(health?.kind).toBe(SymbolKind.Field);
      expect(health?.dataType).toBe('int');
      expect(health?.signature).toContain('var health: int = 100');

      const playerName = symbols.find(s => s.name === 'player_name');
      expect(playerName).toBeDefined();
      expect(playerName?.dataType).toBe('String');

      // Export variables
      const speed = symbols.find(s => s.name === 'speed');
      expect(speed).toBeDefined();
      expect(speed?.signature).toContain('@export var speed: float = 200.0');
      expect(speed?.visibility).toBe('public');

      const armor = symbols.find(s => s.name === 'armor');
      expect(armor).toBeDefined();
      expect(armor?.signature).toContain('@export_range(0, 100)');

      // Legacy export
      const legacySpeed = symbols.find(s => s.name === 'legacy_speed');
      expect(legacySpeed).toBeDefined();
      expect(legacySpeed?.signature).toContain('export var legacy_speed');

      // OnReady variables
      const sprite = symbols.find(s => s.name === 'sprite');
      expect(sprite).toBeDefined();
      expect(sprite?.signature).toContain('@onready var sprite: Sprite2D');
      expect(sprite?.dataType).toBe('Sprite2D');

      // Constants
      const maxLives = symbols.find(s => s.name === 'MAX_LIVES');
      expect(maxLives).toBeDefined();
      expect(maxLives?.kind).toBe(SymbolKind.Constant);
      expect(maxLives?.signature).toContain('const MAX_LIVES: int = 3');

      // Enums
      const stateEnum = symbols.find(s => s.name === 'State');
      expect(stateEnum).toBeDefined();
      expect(stateEnum?.kind).toBe(SymbolKind.Enum);

      const directionEnum = symbols.find(s => s.name === 'Direction');
      expect(directionEnum).toBeDefined();

      // Enum values
      const idle = symbols.find(s => s.name === 'IDLE' && s.parentId === stateEnum?.id);
      expect(idle).toBeDefined();
      expect(idle?.kind).toBe(SymbolKind.EnumMember);

      // Static variables
      const instanceCount = symbols.find(s => s.name === 'instance_count');
      expect(instanceCount).toBeDefined();
      expect(instanceCount?.signature).toContain('static var instance_count');

      // Setget properties
      const score = symbols.find(s => s.name === '_score');
      expect(score).toBeDefined();
      expect(score?.signature).toContain('setget set_score, get_score');

      const setScore = symbols.find(s => s.name === 'set_score');
      expect(setScore).toBeDefined();
      expect(setScore?.kind).toBe(SymbolKind.Method);

      // Modern property syntax
      const level = symbols.find(s => s.name === 'level');
      expect(level).toBeDefined();
      expect(level?.signature).toContain('var level: int = 1:');
    });
  });

  describe('Functions and Signals', () => {
    it('should extract function definitions, built-in callbacks, and signal declarations', async () => {
      const gdCode = `
extends Node2D

# Signal declarations
signal health_changed(new_health: int)
signal player_died
signal item_collected(item_name: String, quantity: int)
signal level_completed(score: int, time: float)

# Built-in lifecycle functions
func _init():
	print("Object initialized")

func _ready():
	print("Node is ready")
	_setup_connections()

func _enter_tree():
	print("Entered scene tree")

func _exit_tree():
	print("Exited scene tree")

func _process(delta: float):
	_update_movement(delta)
	_check_boundaries()

func _physics_process(delta: float):
	_apply_physics(delta)

func _input(event: InputEvent):
	if event is InputEventKey:
		_handle_key_input(event)

func _unhandled_input(event: InputEvent):
	if event is InputEventMouseButton:
		_handle_mouse_click(event)

# Custom functions with various signatures
func simple_function():
	print("Simple function called")

func function_with_params(name: String, age: int, active: bool = true):
	print("Name: %s, Age: %d, Active: %s" % [name, age, active])

func function_with_return(x: float, y: float) -> Vector2:
	return Vector2(x, y)

func function_with_complex_return(data: Array) -> Dictionary:
	var result: Dictionary = {}
	for item in data:
		if item is String:
			result[item] = item.length()
	return result

# Static functions
static func calculate_distance(a: Vector2, b: Vector2) -> float:
	return a.distance_to(b)

static func create_random_color() -> Color:
	return Color(randf(), randf(), randf())

# Virtual functions
func _can_drop_data(position: Vector2, data) -> bool:
	return data is Dictionary and data.has("item_type")

func _drop_data(position: Vector2, data):
	if data.has("item_type"):
		_spawn_item(data.item_type, position)

# Private/internal functions (convention)
func _setup_connections():
	health_changed.connect(_on_health_changed)
	connect("player_died", _on_player_died)

func _update_movement(delta: float):
	var input_vector: Vector2 = Vector2.ZERO

	if Input.is_action_pressed("move_left"):
		input_vector.x -= 1
	if Input.is_action_pressed("move_right"):
		input_vector.x += 1
	if Input.is_action_pressed("move_up"):
		input_vector.y -= 1
	if Input.is_action_pressed("move_down"):
		input_vector.y += 1

	position += input_vector.normalized() * speed * delta

func _apply_physics(delta: float):
	velocity.y += gravity * delta
	velocity = move_and_slide(velocity)

# Signal handlers (conventional naming)
func _on_health_changed(new_health: int):
	if new_health <= 0:
		player_died.emit()

func _on_player_died():
	print("Game Over!")
	get_tree().change_scene_to_file("res://scenes/GameOver.tscn")

func _on_area_2d_body_entered(body: Node2D):
	if body.is_in_group("player"):
		item_collected.emit("coin", 1)

func _on_timer_timeout():
	_spawn_enemy()

# Coroutine functions
func fade_out(duration: float = 1.0):
	var tween: Tween = create_tween()
	tween.tween_property(self, "modulate:a", 0.0, duration)
	await tween.finished

func move_to_position(target: Vector2, duration: float = 2.0):
	var tween: Tween = create_tween()
	tween.tween_property(self, "global_position", target, duration)
	await tween.finished

func async_load_scene(path: String):
	var loader: ResourceLoader = ResourceLoader.load_threaded_request(path)
	while ResourceLoader.load_threaded_get_status(path) != ResourceLoader.THREAD_LOAD_LOADED:
		await get_tree().process_frame
	return ResourceLoader.load_threaded_get(path)

# Function with yield (GDScript 3.x style)
func old_style_coroutine():
	print("Starting coroutine")
	yield(get_tree().create_timer(1.0), "timeout")
	print("Coroutine continued after 1 second")

# Lambda/anonymous functions (GDScript 4.0+)
func use_lambdas():
	var numbers: Array[int] = [1, 2, 3, 4, 5]

	var doubled = numbers.map(func(x): return x * 2)
	var evens = numbers.filter(func(x): return x % 2 == 0)
	var sum = numbers.reduce(func(acc, x): return acc + x, 0)

# Function overloading simulation
func attack():
	_perform_basic_attack()

func attack(target: Node2D):
	_perform_targeted_attack(target)

func attack(target: Node2D, damage: int):
	_perform_custom_attack(target, damage)

# Function with complex parameter types
func process_data(
	items: Array[Dictionary],
	config: Dictionary,
	callback: Callable = Callable()
) -> Array[String]:
	var results: Array[String] = []

	for item in items:
		if _validate_item(item, config):
			var processed: String = _process_item(item)
			results.append(processed)

			if callback.is_valid():
				callback.call(processed)

	return results

# Nested function definitions (inner functions)
func outer_function(data: Array):
	var processed_count: int = 0

	func inner_processor(item):
		processed_count += 1
		return str(item).to_upper()

	var results: Array = []
	for item in data:
		results.append(inner_processor(item))

	print("Processed %d items" % processed_count)
	return results
`;

      const result = await parserManager.parseFile('functions.gd', gdCode);
      const extractor = new GDScriptExtractor('gdscript', 'functions.gd', gdCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Signal declarations
      const healthChanged = symbols.find(s => s.name === 'health_changed');
      expect(healthChanged).toBeDefined();
      expect(healthChanged?.kind).toBe(SymbolKind.Event);
      expect(healthChanged?.signature).toContain('signal health_changed(new_health: int)');

      const playerDied = symbols.find(s => s.name === 'player_died');
      expect(playerDied).toBeDefined();
      expect(playerDied?.kind).toBe(SymbolKind.Event);

      const itemCollected = symbols.find(s => s.name === 'item_collected');
      expect(itemCollected).toBeDefined();
      expect(itemCollected?.signature).toContain('signal item_collected(item_name: String, quantity: int)');

      // Built-in lifecycle functions
      const init = symbols.find(s => s.name === '_init');
      expect(init).toBeDefined();
      expect(init?.kind).toBe(SymbolKind.Constructor);

      const ready = symbols.find(s => s.name === '_ready');
      expect(ready).toBeDefined();
      expect(ready?.kind).toBe(SymbolKind.Method);
      expect(ready?.signature).toContain('func _ready()');

      const process = symbols.find(s => s.name === '_process');
      expect(process).toBeDefined();
      expect(process?.signature).toContain('func _process(delta: float)');

      const physicsProcess = symbols.find(s => s.name === '_physics_process');
      expect(physicsProcess).toBeDefined();

      const input = symbols.find(s => s.name === '_input');
      expect(input).toBeDefined();
      expect(input?.signature).toContain('func _input(event: InputEvent)');

      // Custom functions
      const simpleFunction = symbols.find(s => s.name === 'simple_function');
      expect(simpleFunction).toBeDefined();
      expect(simpleFunction?.kind).toBe(SymbolKind.Function);

      const functionWithParams = symbols.find(s => s.name === 'function_with_params');
      expect(functionWithParams).toBeDefined();
      expect(functionWithParams?.signature).toContain('func function_with_params(name: String, age: int, active: bool = true)');

      const functionWithReturn = symbols.find(s => s.name === 'function_with_return');
      expect(functionWithReturn).toBeDefined();
      expect(functionWithReturn?.signature).toContain('-> Vector2');

      // Static functions
      const calculateDistance = symbols.find(s => s.name === 'calculate_distance');
      expect(calculateDistance).toBeDefined();
      expect(calculateDistance?.signature).toContain('static func calculate_distance');

      // Virtual functions
      const canDropData = symbols.find(s => s.name === '_can_drop_data');
      expect(canDropData).toBeDefined();
      expect(canDropData?.signature).toContain('-> bool');

      // Private functions (convention)
      const setupConnections = symbols.find(s => s.name === '_setup_connections');
      expect(setupConnections).toBeDefined();
      expect(setupConnections?.visibility).toBe('private');

      const updateMovement = symbols.find(s => s.name === '_update_movement');
      expect(updateMovement).toBeDefined();

      // Signal handlers
      const onHealthChanged = symbols.find(s => s.name === '_on_health_changed');
      expect(onHealthChanged).toBeDefined();
      expect(onHealthChanged?.signature).toContain('func _on_health_changed(new_health: int)');

      const onPlayerDied = symbols.find(s => s.name === '_on_player_died');
      expect(onPlayerDied).toBeDefined();

      const onAreaBodyEntered = symbols.find(s => s.name === '_on_area_2d_body_entered');
      expect(onAreaBodyEntered).toBeDefined();

      // Coroutine functions
      const fadeOut = symbols.find(s => s.name === 'fade_out');
      expect(fadeOut).toBeDefined();
      expect(fadeOut?.signature).toContain('func fade_out(duration: float = 1.0)');

      const moveToPosition = symbols.find(s => s.name === 'move_to_position');
      expect(moveToPosition).toBeDefined();

      const asyncLoadScene = symbols.find(s => s.name === 'async_load_scene');
      expect(asyncLoadScene).toBeDefined();

      // Old style coroutine
      const oldStyleCoroutine = symbols.find(s => s.name === 'old_style_coroutine');
      expect(oldStyleCoroutine).toBeDefined();

      // Lambda usage function
      const useLambdas = symbols.find(s => s.name === 'use_lambdas');
      expect(useLambdas).toBeDefined();

      // Function overloading
      const attackFunctions = symbols.filter(s => s.name === 'attack');
      expect(attackFunctions.length).toBeGreaterThanOrEqual(1);

      // Complex parameter function
      const processData = symbols.find(s => s.name === 'process_data');
      expect(processData).toBeDefined();
      expect(processData?.signature).toContain('Array[Dictionary]');
      expect(processData?.signature).toContain('-> Array[String]');

      // Outer function with nested function
      const outerFunction = symbols.find(s => s.name === 'outer_function');
      expect(outerFunction).toBeDefined();

      // Inner function should be detected
      const innerProcessor = symbols.find(s => s.name === 'inner_processor' && s.parentId === outerFunction?.id);
      expect(innerProcessor).toBeDefined();
      expect(innerProcessor?.kind).toBe(SymbolKind.Function);
    });
  });

  describe('Game-Specific Features and Node System', () => {
    it('should extract game development patterns, node references, and Godot-specific constructs', async () => {
      const gdCode = `
extends Control

# Node references and paths
@onready var player: CharacterBody2D = $Player
@onready var ui_manager: UIManager = $"UI/UIManager"
@onready var camera: Camera2D = $Player/Camera2D
@onready var world_environment: WorldEnvironment = $WorldEnvironment

# Resource preloading
const PlayerScene: PackedScene = preload("res://scenes/Player.tscn")
const EnemyScene: PackedScene = preload("res://scenes/Enemy.tscn")
const BulletScene: PackedScene = preload("res://scenes/Bullet.tscn")

# Resource loading
var enemy_texture: Texture2D = load("res://textures/enemy.png")
var background_music: AudioStream = load("res://audio/background.ogg")

# Game state management
enum GameState {
	MENU,
	PLAYING,
	PAUSED,
	GAME_OVER
}

var current_state: GameState = GameState.MENU
var score: int = 0
var level: int = 1
var lives: int = 3

# Input handling
func _input(event: InputEvent):
	match event:
		InputEventKey():
			_handle_keyboard_input(event)
		InputEventMouseButton():
			_handle_mouse_input(event)
		InputEventJoypadButton():
			_handle_controller_input(event)

func _handle_keyboard_input(event: InputEventKey):
	if event.pressed:
		match event.keycode:
			KEY_SPACE:
				_shoot()
			KEY_P:
				_toggle_pause()
			KEY_ESCAPE:
				_open_menu()

func _unhandled_key_input(event: InputEventKey):
	if event.pressed and event.keycode == KEY_F11:
		_toggle_fullscreen()

# Scene management
func change_scene(scene_path: String):
	get_tree().change_scene_to_file(scene_path)

func load_scene_async(scene_path: String):
	ResourceLoader.load_threaded_request(scene_path)
	var progress: Array = []

	while true:
		var status = ResourceLoader.load_threaded_get_status(scene_path, progress)
		match status:
			ResourceLoader.THREAD_LOAD_LOADED:
				var scene = ResourceLoader.load_threaded_get(scene_path)
				get_tree().change_scene_to_packed(scene)
				break
			ResourceLoader.THREAD_LOAD_FAILED:
				print("Failed to load scene: ", scene_path)
				break

		await get_tree().process_frame

# Node manipulation
func spawn_enemy(position: Vector2):
	var enemy: Node2D = EnemyScene.instantiate()
	enemy.global_position = position
	get_parent().add_child(enemy)

	# Connect enemy signals
	enemy.connect("enemy_died", _on_enemy_died)
	enemy.connect("player_hit", _on_player_hit)

func find_nodes_by_group(group_name: String) -> Array[Node]:
	return get_tree().get_nodes_in_group(group_name)

func cleanup_expired_objects():
	var bullets = get_tree().get_nodes_in_group("bullets")
	for bullet in bullets:
		if bullet.has_method("is_expired") and bullet.is_expired():
			bullet.queue_free()

# Physics and collision
func _on_area_2d_body_entered(body: Node2D):
	if body.is_in_group("player"):
		_collect_powerup()
	elif body.is_in_group("enemy"):
		_damage_enemy(body)

func _on_rigid_body_2d_body_entered(body: Node, from: RigidBody2D):
	var collision_force: float = from.linear_velocity.length()
	if collision_force > 100.0:
		_create_explosion(from.global_position)

# Animation and tweening
func animate_ui_element(element: Control, target_position: Vector2):
	var tween: Tween = create_tween()
	tween.parallel().tween_property(element, "position", target_position, 0.5)
	tween.parallel().tween_property(element, "modulate:a", 1.0, 0.3)
	await tween.finished

func shake_camera(intensity: float, duration: float):
	var camera: Camera2D = get_viewport().get_camera_2d()
	var original_position: Vector2 = camera.global_position

	var shake_timer: float = 0.0
	while shake_timer < duration:
		var offset: Vector2 = Vector2(
			randf_range(-intensity, intensity),
			randf_range(-intensity, intensity)
		)
		camera.global_position = original_position + offset
		shake_timer += get_process_delta_time()
		await get_tree().process_frame

	camera.global_position = original_position

# Audio management
@onready var audio_manager: AudioStreamPlayer = $AudioManager
@onready var sfx_player: AudioStreamPlayer2D = $SFXPlayer

func play_sound(sound_stream: AudioStream, volume: float = 0.0):
	sfx_player.stream = sound_stream
	sfx_player.volume_db = volume
	sfx_player.play()

func play_music(music_stream: AudioStream, fade_in: bool = false):
	if fade_in:
		audio_manager.volume_db = -80.0
		audio_manager.stream = music_stream
		audio_manager.play()

		var tween: Tween = create_tween()
		tween.tween_property(audio_manager, "volume_db", 0.0, 2.0)
	else:
		audio_manager.stream = music_stream
		audio_manager.play()

# Save/Load system
const SAVE_FILE: String = "user://savegame.save"

func save_game():
	var save_dict: Dictionary = {
		"player_name": player_name,
		"level": level,
		"score": score,
		"position": player.global_position,
		"inventory": inventory.get_items(),
		"timestamp": Time.get_unix_time_from_system()
	}

	var save_file: FileAccess = FileAccess.open(SAVE_FILE, FileAccess.WRITE)
	if save_file:
		save_file.store_string(JSON.stringify(save_dict))
		save_file.close()
		return true
	return false

func load_game() -> bool:
	if not FileAccess.file_exists(SAVE_FILE):
		return false

	var save_file: FileAccess = FileAccess.open(SAVE_FILE, FileAccess.READ)
	if save_file:
		var json_string: String = save_file.get_as_text()
		save_file.close()

		var json: JSON = JSON.new()
		var parse_result: Error = json.parse(json_string)

		if parse_result == OK:
			var save_dict: Dictionary = json.data
			player_name = save_dict.get("player_name", "Unknown")
			level = save_dict.get("level", 1)
			score = save_dict.get("score", 0)
			player.global_position = save_dict.get("position", Vector2.ZERO)
			return true

	return false

# Particle and visual effects
func create_explosion(position: Vector2, scale: float = 1.0):
	var explosion: GPUParticles2D = preload("res://effects/Explosion.tscn").instantiate()
	explosion.global_position = position
	explosion.process_material.scale_min = scale
	explosion.process_material.scale_max = scale * 1.5
	get_parent().add_child(explosion)

	explosion.emitting = true
	await explosion.finished
	explosion.queue_free()

# Custom drawing
func _draw():
	if debug_mode:
		_draw_debug_info()

func _draw_debug_info():
	var viewport_size: Vector2 = get_viewport_rect().size
	draw_rect(Rect2(Vector2.ZERO, viewport_size), Color.RED, false, 2.0)

	# Draw collision shapes
	for body in get_tree().get_nodes_in_group("enemies"):
		if body is RigidBody2D:
			var shape: CollisionShape2D = body.get_node("CollisionShape2D")
			if shape and shape.shape is RectangleShape2D:
				var rect_shape: RectangleShape2D = shape.shape
				var rect: Rect2 = Rect2(
					body.global_position - rect_shape.size / 2,
					rect_shape.size
				)
				draw_rect(rect, Color.YELLOW, false, 1.0)

# Signals and connections
signal game_state_changed(new_state: GameState)
signal score_updated(new_score: int)
signal level_completed(level_number: int, completion_time: float)

func _connect_signals():
	game_state_changed.connect(_on_game_state_changed)
	score_updated.connect(_on_score_updated)

	# Connect to scene tree signals
	get_tree().node_added.connect(_on_node_added)
	get_tree().node_removed.connect(_on_node_removed)

func _on_game_state_changed(new_state: GameState):
	match new_state:
		GameState.MENU:
			_show_main_menu()
		GameState.PLAYING:
			_start_gameplay()
		GameState.PAUSED:
			_pause_game()
		GameState.GAME_OVER:
			_show_game_over()

# Threading and async operations
func process_large_dataset(data: Array):
	var thread: Thread = Thread.new()
	thread.start(_background_processing.bind(data))

	while thread.is_alive():
		await get_tree().process_frame

	var result = thread.wait_to_finish()
	return result

func _background_processing(data: Array):
	var processed: Array = []
	for item in data:
		# Simulate heavy processing
		var result = _complex_calculation(item)
		processed.append(result)
	return processed
`;

      const result = await parserManager.parseFile('game.gd', gdCode);
      const extractor = new GDScriptExtractor('gdscript', 'game.gd', gdCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Node references
      const player = symbols.find(s => s.name === 'player');
      expect(player).toBeDefined();
      expect(player?.dataType).toBe('CharacterBody2D');
      expect(player?.signature).toContain('@onready var player: CharacterBody2D = $Player');

      const uiManager = symbols.find(s => s.name === 'ui_manager');
      expect(uiManager).toBeDefined();
      expect(uiManager?.dataType).toBe('UIManager');

      const camera = symbols.find(s => s.name === 'camera');
      expect(camera).toBeDefined();
      expect(camera?.signature).toContain('$Player/Camera2D');

      // Resource preloading
      const playerScene = symbols.find(s => s.name === 'PlayerScene');
      expect(playerScene).toBeDefined();
      expect(playerScene?.kind).toBe(SymbolKind.Constant);
      expect(playerScene?.signature).toContain('preload("res://scenes/Player.tscn")');

      // Resource loading
      const enemyTexture = symbols.find(s => s.name === 'enemy_texture');
      expect(enemyTexture).toBeDefined();
      expect(enemyTexture?.signature).toContain('load("res://textures/enemy.png")');

      // Game state enum
      const gameState = symbols.find(s => s.name === 'GameState');
      expect(gameState).toBeDefined();
      expect(gameState?.kind).toBe(SymbolKind.Enum);

      // Game state variables
      const currentState = symbols.find(s => s.name === 'current_state');
      expect(currentState).toBeDefined();
      expect(currentState?.dataType).toBe('GameState');

      // Input handling
      const handleKeyboardInput = symbols.find(s => s.name === '_handle_keyboard_input');
      expect(handleKeyboardInput).toBeDefined();
      expect(handleKeyboardInput?.signature).toContain('func _handle_keyboard_input(event: InputEventKey)');

      const unhandledKeyInput = symbols.find(s => s.name === '_unhandled_key_input');
      expect(unhandledKeyInput).toBeDefined();

      // Scene management
      const changeScene = symbols.find(s => s.name === 'change_scene');
      expect(changeScene).toBeDefined();

      const loadSceneAsync = symbols.find(s => s.name === 'load_scene_async');
      expect(loadSceneAsync).toBeDefined();

      // Node manipulation
      const spawnEnemy = symbols.find(s => s.name === 'spawn_enemy');
      expect(spawnEnemy).toBeDefined();
      expect(spawnEnemy?.signature).toContain('func spawn_enemy(position: Vector2)');

      const findNodesByGroup = symbols.find(s => s.name === 'find_nodes_by_group');
      expect(findNodesByGroup).toBeDefined();
      expect(findNodesByGroup?.signature).toContain('-> Array[Node]');

      // Physics callbacks
      const onAreaBodyEntered = symbols.find(s => s.name === '_on_area_2d_body_entered');
      expect(onAreaBodyEntered).toBeDefined();

      const onRigidBodyEntered = symbols.find(s => s.name === '_on_rigid_body_2d_body_entered');
      expect(onRigidBodyEntered).toBeDefined();

      // Animation functions
      const animateUiElement = symbols.find(s => s.name === 'animate_ui_element');
      expect(animateUiElement).toBeDefined();

      const shakeCamera = symbols.find(s => s.name === 'shake_camera');
      expect(shakeCamera).toBeDefined();

      // Audio management
      const audioManager = symbols.find(s => s.name === 'audio_manager');
      expect(audioManager).toBeDefined();
      expect(audioManager?.dataType).toBe('AudioStreamPlayer');

      const playSound = symbols.find(s => s.name === 'play_sound');
      expect(playSound).toBeDefined();

      const playMusic = symbols.find(s => s.name === 'play_music');
      expect(playMusic).toBeDefined();

      // Save/Load system
      const saveFile = symbols.find(s => s.name === 'SAVE_FILE');
      expect(saveFile).toBeDefined();
      expect(saveFile?.kind).toBe(SymbolKind.Constant);

      const saveGame = symbols.find(s => s.name === 'save_game');
      expect(saveGame).toBeDefined();

      const loadGame = symbols.find(s => s.name === 'load_game');
      expect(loadGame).toBeDefined();
      expect(loadGame?.signature).toContain('-> bool');

      // Visual effects
      const createExplosion = symbols.find(s => s.name === 'create_explosion');
      expect(createExplosion).toBeDefined();

      // Custom drawing
      const draw = symbols.find(s => s.name === '_draw');
      expect(draw).toBeDefined();

      const drawDebugInfo = symbols.find(s => s.name === '_draw_debug_info');
      expect(drawDebugInfo).toBeDefined();

      // Signal declarations
      const gameStateChanged = symbols.find(s => s.name === 'game_state_changed');
      expect(gameStateChanged).toBeDefined();
      expect(gameStateChanged?.kind).toBe(SymbolKind.Event);

      const scoreUpdated = symbols.find(s => s.name === 'score_updated');
      expect(scoreUpdated).toBeDefined();

      // Signal handling
      const connectSignals = symbols.find(s => s.name === '_connect_signals');
      expect(connectSignals).toBeDefined();

      const onGameStateChanged = symbols.find(s => s.name === '_on_game_state_changed');
      expect(onGameStateChanged).toBeDefined();

      // Threading
      const processLargeDataset = symbols.find(s => s.name === 'process_large_dataset');
      expect(processLargeDataset).toBeDefined();

      const backgroundProcessing = symbols.find(s => s.name === '_background_processing');
      expect(backgroundProcessing).toBeDefined();
      expect(backgroundProcessing?.visibility).toBe('private');
    });
  });

  describe('Advanced GDScript Features', () => {
    it('should extract annotations, type hints, generics, and modern GDScript constructs', async () => {
      const gdCode = `
extends RefCounted

# Advanced type hints and generics
var typed_array: Array[String] = []
var typed_dict: Dictionary = {}
var vector_array: Array[Vector2] = []
var node_array: Array[Node] = []

# Optional and nullable types
var optional_texture: Texture2D
var nullable_node: Node
var maybe_string: String = ""

# Advanced annotations
@export_category("Player Settings")
@export var player_speed: float = 100.0

@export_group("Combat")
@export var damage: int = 10
@export var critical_chance: float = 0.1

@export_subgroup("Weapons")
@export var weapon_damage: int = 25
@export var weapon_range: float = 50.0

@export_enum("Easy", "Medium", "Hard", "Nightmare") var difficulty: int = 1
@export_flags("Fire:1", "Water:2", "Earth:4", "Air:8") var elements: int = 0

@export_file("*.json") var config_file: String
@export_dir var save_directory: String
@export_global_file("*.tscn") var scene_file: String

@export_multiline var description: String = ""
@export_placeholder("Enter your name") var player_name: String = ""

# Custom resource types
@export var custom_resource: CustomPlayerData
@export var packed_scene: PackedScene

# Tool script functionality
@tool
extends EditorPlugin

var editor_interface: EditorInterface

func _enter_tree():
	editor_interface = get_editor_interface()
	add_custom_type(
		"CustomNode",
		"Node2D",
		preload("res://scripts/CustomNode.gd"),
		preload("res://icons/custom_node.png")
	)

func _exit_tree():
	remove_custom_type("CustomNode")

# Match statements (GDScript 4.0+)
func handle_input_action(action: String):
	match action:
		"move_left", "move_right":
			_handle_movement(action)
		"jump" when is_on_floor():
			_handle_jump()
		"attack" when can_attack:
			_handle_attack()
		var unknown_action:
			print("Unknown action: ", unknown_action)

func process_value(value):
	match typeof(value):
		TYPE_INT:
			return value * 2
		TYPE_FLOAT:
			return round(value)
		TYPE_STRING:
			return value.to_upper()
		TYPE_ARRAY:
			return value.size()
		_:
			return null

# Advanced class features
class_name AdvancedPlayer
extends CharacterBody2D

# Interface-like implementation using duck typing
interface IMovable:
	func move(direction: Vector2)
	func stop()
	func get_speed() -> float

interface IDamageable:
	func take_damage(amount: int)
	func heal(amount: int)
	func is_alive() -> bool

# Multiple "interface" implementation
extends Node2D
# implements IMovable, IDamageable  # Note: GDScript doesn't have formal interfaces

func move(direction: Vector2):
	position += direction * speed

func stop():
	velocity = Vector2.ZERO

func get_speed() -> float:
	return speed

func take_damage(amount: int):
	health -= amount

func heal(amount: int):
	health = min(health + amount, max_health)

func is_alive() -> bool:
	return health > 0

# Lambda expressions and functional programming
func use_advanced_lambdas():
	var numbers: Array[int] = range(1, 11)

	# Complex lambda with multiple operations
	var processed = numbers.map(func(x):
		var squared = x * x
		var result = squared + 10
		return result if result % 2 == 0 else 0
	)

	# Lambda with closure
	var multiplier: int = 5
	var multiplied = numbers.map(func(x): return x * multiplier)

	# Chained operations
	var result = numbers\
		.filter(func(x): return x % 2 == 0)\
		.map(func(x): return x * x)\
		.reduce(func(acc, x): return acc + x, 0)

# Async/await patterns
func complex_async_operation():
	print("Starting complex operation...")

	# Parallel async operations
	var task1 = fetch_data_async("api/users")
	var task2 = fetch_data_async("api/settings")
	var task3 = load_texture_async("res://images/background.png")

	# Wait for all to complete
	var users = await task1
	var settings = await task2
	var texture = await task3

	return {
		"users": users,
		"settings": settings,
		"texture": texture
	}

func fetch_data_async(endpoint: String):
	var http_request: HTTPRequest = HTTPRequest.new()
	add_child(http_request)

	var url = "https://api.example.com/" + endpoint
	http_request.request(url)

	var response = await http_request.request_completed
	http_request.queue_free()

	return JSON.parse_string(response[3].get_string_from_utf8())

# Custom property accessor with advanced logic
var _energy: float = 100.0
var energy: float:
	get:
		return _energy
	set(value):
		var old_energy = _energy
		_energy = clampf(value, 0.0, 100.0)

		if _energy != old_energy:
			energy_changed.emit(_energy, old_energy)

		if _energy == 0.0 and old_energy > 0.0:
			energy_depleted.emit()
		elif _energy == 100.0 and old_energy < 100.0:
			energy_full.emit()

signal energy_changed(new_value: float, old_value: float)
signal energy_depleted
signal energy_full

# Advanced generic types and type checking
func process_collection[T](items: Array[T], processor: Callable) -> Array[T]:
	var results: Array[T] = []
	for item in items:
		if processor.is_valid():
			results.append(processor.call(item))
		else:
			results.append(item)
	return results

func safe_cast[T](object: Variant, type: T) -> T:
	if object is T:
		return object as T
	else:
		return null

# Custom iterators
class NumberRange:
	var start: int
	var end: int
	var current: int

	func _init(start_val: int, end_val: int):
		start = start_val
		end = end_val
		current = start

	func _iter_init(_arg):
		current = start
		return current <= end

	func _iter_next(_arg):
		current += 1
		return current <= end

	func _iter_get(_arg):
		return current

# Usage of custom iterator
func use_custom_iterator():
	var range_obj = NumberRange.new(1, 5)
	for number in range_obj:
		print("Number: ", number)

# Advanced signal connections with parameters
func setup_advanced_connections():
	# Connect with additional parameters
	player_died.connect(_on_player_died.bind("game_over_screen"))

	# Connect to lambda
	enemy_spawned.connect(func(enemy):
		enemy.add_to_group("enemies")
		enemy.set_target(player)
	)

	# One-shot connections
	level_completed.connect(_on_level_completed, CONNECT_ONE_SHOT)

	# Deferred connections
	ui_updated.connect(_on_ui_updated, CONNECT_DEFERRED)

# Resource management and cleanup
func _notification(what: int):
	match what:
		NOTIFICATION_PREDELETE:
			_cleanup_resources()
		NOTIFICATION_WM_CLOSE_REQUEST:
			_save_before_exit()
		NOTIFICATION_APPLICATION_FOCUS_OUT:
			_pause_background_processes()

func _cleanup_resources():
	for resource in managed_resources:
		if resource.has_method("cleanup"):
			resource.cleanup()
`;

      const result = await parserManager.parseFile('advanced.gd', gdCode);
      const extractor = new GDScriptExtractor('gdscript', 'advanced.gd', gdCode);
      const symbols = extractor.extractSymbols(result.tree);

      // Advanced type hints
      const typedArray = symbols.find(s => s.name === 'typed_array');
      expect(typedArray).toBeDefined();
      expect(typedArray?.signature).toContain('var typed_array: Array[String]');
      expect(typedArray?.dataType).toBe('Array[String]');

      const vectorArray = symbols.find(s => s.name === 'vector_array');
      expect(vectorArray).toBeDefined();
      expect(vectorArray?.dataType).toBe('Array[Vector2]');

      // Export annotations
      const playerSpeed = symbols.find(s => s.name === 'player_speed');
      expect(playerSpeed).toBeDefined();
      expect(playerSpeed?.signature).toContain('@export_category("Player Settings")');

      const damage = symbols.find(s => s.name === 'damage');
      expect(damage).toBeDefined();
      expect(damage?.signature).toContain('@export_group("Combat")');

      const weaponDamage = symbols.find(s => s.name === 'weapon_damage');
      expect(weaponDamage).toBeDefined();
      expect(weaponDamage?.signature).toContain('@export_subgroup("Weapons")');

      const difficulty = symbols.find(s => s.name === 'difficulty');
      expect(difficulty).toBeDefined();
      expect(difficulty?.signature).toContain('@export_enum');

      const elements = symbols.find(s => s.name === 'elements');
      expect(elements).toBeDefined();
      expect(elements?.signature).toContain('@export_flags');

      const configFile = symbols.find(s => s.name === 'config_file');
      expect(configFile).toBeDefined();
      expect(configFile?.signature).toContain('@export_file("*.json")');

      const description = symbols.find(s => s.name === 'description');
      expect(description).toBeDefined();
      expect(description?.signature).toContain('@export_multiline');

      // Tool script
      const editorInterface = symbols.find(s => s.name === 'editor_interface');
      expect(editorInterface).toBeDefined();

      const enterTree = symbols.find(s => s.name === '_enter_tree');
      expect(enterTree).toBeDefined();

      const exitTree = symbols.find(s => s.name === '_exit_tree');
      expect(exitTree).toBeDefined();

      // Match statements
      const handleInputAction = symbols.find(s => s.name === 'handle_input_action');
      expect(handleInputAction).toBeDefined();

      const processValue = symbols.find(s => s.name === 'process_value');
      expect(processValue).toBeDefined();

      // Advanced class
      const advancedPlayer = symbols.find(s => s.name === 'AdvancedPlayer');
      expect(advancedPlayer).toBeDefined();
      expect(advancedPlayer?.kind).toBe(SymbolKind.Class);

      // Interface-like methods
      const move = symbols.find(s => s.name === 'move');
      expect(move).toBeDefined();
      expect(move?.signature).toContain('func move(direction: Vector2)');

      const takeDamage = symbols.find(s => s.name === 'take_damage');
      expect(takeDamage).toBeDefined();

      const isAlive = symbols.find(s => s.name === 'is_alive');
      expect(isAlive).toBeDefined();
      expect(isAlive?.signature).toContain('-> bool');

      // Lambda usage
      const useAdvancedLambdas = symbols.find(s => s.name === 'use_advanced_lambdas');
      expect(useAdvancedLambdas).toBeDefined();

      // Async operations
      const complexAsyncOperation = symbols.find(s => s.name === 'complex_async_operation');
      expect(complexAsyncOperation).toBeDefined();

      const fetchDataAsync = symbols.find(s => s.name === 'fetch_data_async');
      expect(fetchDataAsync).toBeDefined();

      // Advanced property with getter/setter
      const energy = symbols.find(s => s.name === 'energy');
      expect(energy).toBeDefined();
      expect(energy?.signature).toContain('var energy: float:');

      // Signals for energy
      const energyChanged = symbols.find(s => s.name === 'energy_changed');
      expect(energyChanged).toBeDefined();
      expect(energyChanged?.kind).toBe(SymbolKind.Event);

      const energyDepleted = symbols.find(s => s.name === 'energy_depleted');
      expect(energyDepleted).toBeDefined();

      // Generic functions
      const processCollection = symbols.find(s => s.name === 'process_collection');
      expect(processCollection).toBeDefined();
      expect(processCollection?.signature).toContain('func process_collection[T]');

      const safeCast = symbols.find(s => s.name === 'safe_cast');
      expect(safeCast).toBeDefined();
      expect(safeCast?.signature).toContain('func safe_cast[T]');

      // Custom iterator class
      const numberRange = symbols.find(s => s.name === 'NumberRange');
      expect(numberRange).toBeDefined();
      expect(numberRange?.kind).toBe(SymbolKind.Class);

      // Iterator methods
      const iterInit = symbols.find(s => s.name === '_iter_init' && s.parentId === numberRange?.id);
      expect(iterInit).toBeDefined();

      const iterNext = symbols.find(s => s.name === '_iter_next' && s.parentId === numberRange?.id);
      expect(iterNext).toBeDefined();

      const iterGet = symbols.find(s => s.name === '_iter_get' && s.parentId === numberRange?.id);
      expect(iterGet).toBeDefined();

      // Iterator usage
      const useCustomIterator = symbols.find(s => s.name === 'use_custom_iterator');
      expect(useCustomIterator).toBeDefined();

      // Advanced connections
      const setupAdvancedConnections = symbols.find(s => s.name === 'setup_advanced_connections');
      expect(setupAdvancedConnections).toBeDefined();

      // Notification handling
      const notification = symbols.find(s => s.name === '_notification');
      expect(notification).toBeDefined();
      expect(notification?.signature).toContain('func _notification(what: int)');

      const cleanupResources = symbols.find(s => s.name === '_cleanup_resources');
      expect(cleanupResources).toBeDefined();
    });
  });

  describe('Resource Management and Serialization', () => {
    it('should extract resource handling, custom resources, and serialization patterns', async () => {
      const gdCode = `
extends Resource
class_name GameData

# Custom resource properties
@export var version: String = "1.0"
@export var player_data: PlayerData
@export var world_data: WorldData
@export var settings: GameSettings

# Resource arrays
@export var levels: Array[LevelData] = []
@export var items: Array[ItemData] = []
@export var achievements: Array[AchievementData] = []

# Resource serialization
func serialize() -> Dictionary:
	return {
		"version": version,
		"player_data": player_data.serialize() if player_data else null,
		"world_data": world_data.serialize() if world_data else null,
		"settings": settings.serialize() if settings else null,
		"levels": levels.map(func(level): return level.serialize()),
		"items": items.map(func(item): return item.serialize()),
		"achievements": achievements.map(func(achievement): return achievement.serialize())
	}

func deserialize(data: Dictionary):
	version = data.get("version", "1.0")

	if data.has("player_data") and data["player_data"]:
		player_data = PlayerData.new()
		player_data.deserialize(data["player_data"])

	if data.has("world_data") and data["world_data"]:
		world_data = WorldData.new()
		world_data.deserialize(data["world_data"])

	if data.has("settings") and data["settings"]:
		settings = GameSettings.new()
		settings.deserialize(data["settings"])

	# Deserialize arrays
	levels.clear()
	for level_data in data.get("levels", []):
		var level = LevelData.new()
		level.deserialize(level_data)
		levels.append(level)

# Custom PlayerData resource
class_name PlayerData
extends Resource

@export var name: String = ""
@export var level: int = 1
@export var experience: int = 0
@export var health: int = 100
@export var mana: int = 50
@export var position: Vector3 = Vector3.ZERO
@export var inventory: InventoryData
@export var stats: CharacterStats
@export var unlocked_abilities: Array[String] = []

# Custom serialization with validation
func serialize() -> Dictionary:
	var data = {
		"name": name,
		"level": level,
		"experience": experience,
		"health": health,
		"mana": mana,
		"position": {"x": position.x, "y": position.y, "z": position.z},
		"unlocked_abilities": unlocked_abilities.duplicate()
	}

	if inventory:
		data["inventory"] = inventory.serialize()

	if stats:
		data["stats"] = stats.serialize()

	return data

func deserialize(data: Dictionary):
	name = data.get("name", "")
	level = max(1, data.get("level", 1))
	experience = max(0, data.get("experience", 0))
	health = clamp(data.get("health", 100), 0, 999)
	mana = clamp(data.get("mana", 50), 0, 999)

	var pos_data = data.get("position", {})
	position = Vector3(
		pos_data.get("x", 0.0),
		pos_data.get("y", 0.0),
		pos_data.get("z", 0.0)
	)

	unlocked_abilities = data.get("unlocked_abilities", []).duplicate()

	if data.has("inventory"):
		inventory = InventoryData.new()
		inventory.deserialize(data["inventory"])

	if data.has("stats"):
		stats = CharacterStats.new()
		stats.deserialize(data["stats"])

# Resource manager singleton
extends Node

var loaded_resources: Dictionary = {}
var resource_cache: Dictionary = {}
var loading_queue: Array = []
var max_cache_size: int = 100

signal resource_loaded(path: String, resource: Resource)
signal resource_failed(path: String, error: String)

func load_resource_async(path: String, type_hint: String = "") -> Resource:
	# Check cache first
	if resource_cache.has(path):
		return resource_cache[path]

	# Check if already loading
	if path in loading_queue:
		while path in loading_queue:
			await get_tree().process_frame
		return resource_cache.get(path)

	loading_queue.append(path)

	# Start threaded loading
	ResourceLoader.load_threaded_request(path, type_hint)

	var progress: Array = []
	while true:
		var status = ResourceLoader.load_threaded_get_status(path, progress)

		match status:
			ResourceLoader.THREAD_LOAD_LOADED:
				var resource = ResourceLoader.load_threaded_get(path)
				_cache_resource(path, resource)
				loading_queue.erase(path)
				resource_loaded.emit(path, resource)
				return resource

			ResourceLoader.THREAD_LOAD_FAILED:
				loading_queue.erase(path)
				resource_failed.emit(path, "Failed to load resource")
				return null

			ResourceLoader.THREAD_LOAD_INVALID_RESOURCE:
				loading_queue.erase(path)
				resource_failed.emit(path, "Invalid resource")
				return null

		await get_tree().process_frame

func _cache_resource(path: String, resource: Resource):
	if resource_cache.size() >= max_cache_size:
		_evict_oldest_resource()

	resource_cache[path] = resource
	loaded_resources[path] = Time.get_unix_time_from_system()

func _evict_oldest_resource():
	var oldest_path: String = ""
	var oldest_time: float = INF

	for path in loaded_resources:
		var load_time = loaded_resources[path]
		if load_time < oldest_time:
			oldest_time = load_time
			oldest_path = path

	if oldest_path:
		resource_cache.erase(oldest_path)
		loaded_resources.erase(oldest_path)

# Configuration resource
class_name GameConfig
extends Resource

# Graphics settings
@export_group("Graphics")
@export var resolution: Vector2i = Vector2i(1920, 1080)
@export var fullscreen: bool = false
@export var vsync: bool = true
@export_range(0.5, 2.0) var render_scale: float = 1.0
@export_enum("Low", "Medium", "High", "Ultra") var quality_preset: int = 2

# Audio settings
@export_group("Audio")
@export_range(0.0, 1.0) var master_volume: float = 1.0
@export_range(0.0, 1.0) var music_volume: float = 0.8
@export_range(0.0, 1.0) var sfx_volume: float = 1.0
@export_range(0.0, 1.0) var voice_volume: float = 1.0

# Input settings
@export_group("Input")
@export var mouse_sensitivity: float = 1.0
@export var invert_y_axis: bool = false
@export var key_bindings: Dictionary = {}

# Gameplay settings
@export_group("Gameplay")
@export var difficulty: int = 1
@export var auto_save: bool = true
@export var save_interval: float = 300.0  # 5 minutes
@export var show_hints: bool = true

func apply_settings():
	_apply_graphics_settings()
	_apply_audio_settings()
	_apply_input_settings()

func _apply_graphics_settings():
	var window = get_window()

	if fullscreen:
		window.mode = Window.MODE_FULLSCREEN
	else:
		window.mode = Window.MODE_WINDOWED
		window.size = resolution

	match vsync:
		true:
			DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_ENABLED)
		false:
			DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)

	get_viewport().scaling_3d_scale = render_scale

func _apply_audio_settings():
	var master_bus = AudioServer.get_bus_index("Master")
	var music_bus = AudioServer.get_bus_index("Music")
	var sfx_bus = AudioServer.get_bus_index("SFX")

	AudioServer.set_bus_volume_db(master_bus, linear_to_db(master_volume))
	AudioServer.set_bus_volume_db(music_bus, linear_to_db(music_volume))
	AudioServer.set_bus_volume_db(sfx_bus, linear_to_db(sfx_volume))

func save_to_file(path: String = "user://settings.cfg"):
	var config = ConfigFile.new()

	config.set_value("graphics", "resolution", resolution)
	config.set_value("graphics", "fullscreen", fullscreen)
	config.set_value("graphics", "vsync", vsync)
	config.set_value("graphics", "render_scale", render_scale)
	config.set_value("graphics", "quality_preset", quality_preset)

	config.set_value("audio", "master_volume", master_volume)
	config.set_value("audio", "music_volume", music_volume)
	config.set_value("audio", "sfx_volume", sfx_volume)
	config.set_value("audio", "voice_volume", voice_volume)

	config.set_value("input", "mouse_sensitivity", mouse_sensitivity)
	config.set_value("input", "invert_y_axis", invert_y_axis)
	config.set_value("input", "key_bindings", key_bindings)

	config.set_value("gameplay", "difficulty", difficulty)
	config.set_value("gameplay", "auto_save", auto_save)
	config.set_value("gameplay", "save_interval", save_interval)
	config.set_value("gameplay", "show_hints", show_hints)

	var error = config.save(path)
	if error != OK:
		print("Failed to save settings: ", error)

func load_from_file(path: String = "user://settings.cfg"):
	var config = ConfigFile.new()
	var error = config.load(path)

	if error != OK:
		print("Failed to load settings, using defaults")
		return

	resolution = config.get_value("graphics", "resolution", Vector2i(1920, 1080))
	fullscreen = config.get_value("graphics", "fullscreen", false)
	vsync = config.get_value("graphics", "vsync", true)
	render_scale = config.get_value("graphics", "render_scale", 1.0)
	quality_preset = config.get_value("graphics", "quality_preset", 2)

	master_volume = config.get_value("audio", "master_volume", 1.0)
	music_volume = config.get_value("audio", "music_volume", 0.8)
	sfx_volume = config.get_value("audio", "sfx_volume", 1.0)
	voice_volume = config.get_value("audio", "voice_volume", 1.0)

	mouse_sensitivity = config.get_value("input", "mouse_sensitivity", 1.0)
	invert_y_axis = config.get_value("input", "invert_y_axis", false)
	key_bindings = config.get_value("input", "key_bindings", {})

	difficulty = config.get_value("gameplay", "difficulty", 1)
	auto_save = config.get_value("gameplay", "auto_save", true)
	save_interval = config.get_value("gameplay", "save_interval", 300.0)
	show_hints = config.get_value("gameplay", "show_hints", true)
`;

      const result = await parserManager.parseFile('resources.gd', gdCode);
      const extractor = new GDScriptExtractor('gdscript', 'resources.gd', gdCode);
      const symbols = extractor.extractSymbols(result.tree);

      // GameData resource class
      const gameData = symbols.find(s => s.name === 'GameData');
      expect(gameData).toBeDefined();
      expect(gameData?.kind).toBe(SymbolKind.Class);
      expect(gameData?.baseClass).toBe('Resource');

      // Resource properties
      const version = symbols.find(s => s.name === 'version');
      expect(version).toBeDefined();
      expect(version?.signature).toContain('@export var version: String = "1.0"');

      const playerData = symbols.find(s => s.name === 'player_data');
      expect(playerData).toBeDefined();
      expect(playerData?.dataType).toBe('PlayerData');

      const levels = symbols.find(s => s.name === 'levels');
      expect(levels).toBeDefined();
      expect(levels?.dataType).toBe('Array[LevelData]');

      // Serialization methods
      const serialize = symbols.find(s => s.name === 'serialize' && s.parentId === gameData?.id);
      expect(serialize).toBeDefined();
      expect(serialize?.kind).toBe(SymbolKind.Method);
      expect(serialize?.signature).toContain('-> Dictionary');

      const deserialize = symbols.find(s => s.name === 'deserialize' && s.parentId === gameData?.id);
      expect(deserialize).toBeDefined();

      // PlayerData class
      const playerDataClass = symbols.find(s => s.name === 'PlayerData');
      expect(playerDataClass).toBeDefined();
      expect(playerDataClass?.kind).toBe(SymbolKind.Class);

      // PlayerData properties
      const name = symbols.find(s => s.name === 'name' && s.parentId === playerDataClass?.id);
      expect(name).toBeDefined();
      expect(name?.dataType).toBe('String');

      const level = symbols.find(s => s.name === 'level' && s.parentId === playerDataClass?.id);
      expect(level).toBeDefined();
      expect(level?.dataType).toBe('int');

      const position = symbols.find(s => s.name === 'position' && s.parentId === playerDataClass?.id);
      expect(position).toBeDefined();
      expect(position?.dataType).toBe('Vector3');

      const unlockedAbilities = symbols.find(s => s.name === 'unlocked_abilities');
      expect(unlockedAbilities).toBeDefined();
      expect(unlockedAbilities?.dataType).toBe('Array[String]');

      // Resource manager properties
      const loadedResources = symbols.find(s => s.name === 'loaded_resources');
      expect(loadedResources).toBeDefined();
      expect(loadedResources?.dataType).toBe('Dictionary');

      const resourceCache = symbols.find(s => s.name === 'resource_cache');
      expect(resourceCache).toBeDefined();

      const loadingQueue = symbols.find(s => s.name === 'loading_queue');
      expect(loadingQueue).toBeDefined();
      expect(loadingQueue?.dataType).toBe('Array');

      // Resource manager signals
      const resourceLoaded = symbols.find(s => s.name === 'resource_loaded');
      expect(resourceLoaded).toBeDefined();
      expect(resourceLoaded?.kind).toBe(SymbolKind.Event);
      expect(resourceLoaded?.signature).toContain('signal resource_loaded(path: String, resource: Resource)');

      const resourceFailed = symbols.find(s => s.name === 'resource_failed');
      expect(resourceFailed).toBeDefined();

      // Resource manager methods
      const loadResourceAsync = symbols.find(s => s.name === 'load_resource_async');
      expect(loadResourceAsync).toBeDefined();
      expect(loadResourceAsync?.signature).toContain('-> Resource');

      const cacheResource = symbols.find(s => s.name === '_cache_resource');
      expect(cacheResource).toBeDefined();
      expect(cacheResource?.visibility).toBe('private');

      const evictOldestResource = symbols.find(s => s.name === '_evict_oldest_resource');
      expect(evictOldestResource).toBeDefined();

      // GameConfig class
      const gameConfig = symbols.find(s => s.name === 'GameConfig');
      expect(gameConfig).toBeDefined();
      expect(gameConfig?.kind).toBe(SymbolKind.Class);

      // Graphics settings
      const resolution = symbols.find(s => s.name === 'resolution' && s.parentId === gameConfig?.id);
      expect(resolution).toBeDefined();
      expect(resolution?.dataType).toBe('Vector2i');

      const fullscreen = symbols.find(s => s.name === 'fullscreen' && s.parentId === gameConfig?.id);
      expect(fullscreen).toBeDefined();
      expect(fullscreen?.dataType).toBe('bool');

      const renderScale = symbols.find(s => s.name === 'render_scale');
      expect(renderScale).toBeDefined();
      expect(renderScale?.signature).toContain('@export_range(0.5, 2.0)');

      const qualityPreset = symbols.find(s => s.name === 'quality_preset');
      expect(qualityPreset).toBeDefined();
      expect(qualityPreset?.signature).toContain('@export_enum');

      // Audio settings
      const masterVolume = symbols.find(s => s.name === 'master_volume');
      expect(masterVolume).toBeDefined();
      expect(masterVolume?.signature).toContain('@export_range(0.0, 1.0)');

      // Input settings
      const mouseSensitivity = symbols.find(s => s.name === 'mouse_sensitivity');
      expect(mouseSensitivity).toBeDefined();

      const keyBindings = symbols.find(s => s.name === 'key_bindings');
      expect(keyBindings).toBeDefined();
      expect(keyBindings?.dataType).toBe('Dictionary');

      // Config methods
      const applySettings = symbols.find(s => s.name === 'apply_settings');
      expect(applySettings).toBeDefined();

      const applyGraphicsSettings = symbols.find(s => s.name === '_apply_graphics_settings');
      expect(applyGraphicsSettings).toBeDefined();
      expect(applyGraphicsSettings?.visibility).toBe('private');

      const applyAudioSettings = symbols.find(s => s.name === '_apply_audio_settings');
      expect(applyAudioSettings).toBeDefined();

      const saveToFile = symbols.find(s => s.name === 'save_to_file');
      expect(saveToFile).toBeDefined();

      const loadFromFile = symbols.find(s => s.name === 'load_from_file');
      expect(loadFromFile).toBeDefined();
    });
  });
});