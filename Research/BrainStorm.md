# Omni Character
This is the creation of a drag and drop ready character animation system.
It is implemented as GDExtenstion targeting
4.5+

## MAIN PILLARS OF DESIGN!!!

- easy to use
- extensible
	- modular
	- bring own animations
- Gameplay actions to drive animations as automatically as possible

## impl design
- Omni Character is a node. It inherits characterController3d
	- I want to be configured from one screen or node.
	- Can contain multiple types of resources

- built upon Input State -> tag set, resolver (what clip(s) to play) and composition (stack multiple actions in layers -> Final pose

### DATA
 - AnimationClips/BlendSpaces
	 - Contain tag data
		- For blendspaces needs scalar bindings
 - layers
 - Tags: data about the character such as equiped weapon, active action, status, locomotion. must be based descrete and not continuous data.
 - Rules:
	 - Checks that can prevent an animationClip from being selected acts as a filter
 - Ability actions:
	 - are gameplay based things the player can do
	 - can have multiple actions occuring at same time ex: walking and shooting
		- return tags and state data
 - style:
	 - affects how actions look (like injured, breathing) has no affect on actual gameplay

### Design Problems:
 - incompatible actions?? actions that affect the same bone?? swimming affects leg bones and so does running???
 	- possible fix is some kind of rule table
  - Rules contain some boolean statement based off character current actions??
- How would a shooting action work with different weapons?
	- makes me think there needs to be a layer above "actions"
	- Actions no longer return direct animation clips. It now adds and removes tags and state data. The resolver uses this to select the clip.
- How does the resolver work?
	- Uses the tags on the clips to choose best match
	- Might be cumbersome to make the correct anim to choose in the right scenerio.
#### Scenario 1:
 Input: W key

Camera Vector: Forward

global_movement_vector: Foward

 ---
 World/Character state: Grounded,

 External forces: none

 Style options : none

 prev_state: same

----

Available actions:

Walk, Run, jump, swim, shoot, hold item, pickup item,

Selected actions based on Input and prev_state: Walk

Walk clips: Walk_F Walk_R Walk_L Walk_Bkwd

Action selectes script based on global_movement_vector calcualted by input and camera vector.

Resolution: Walk_F -> Walk_F or continues in this case

#### Scenario 2: change direction from forward to foward left

Input: W and A key

Camera Vector: Forward

global_movement_vector: Foward_LEFT

 ---
 World/Character state: Grounded,

 External forces: none

 Style options : none

 prev_state: FOWARD, WALK_F

----

Available actions:

Walk, Run, jump, swim, shoot, hold item, pickup item,

Selected actions based on Input and prev_state: Walk

Walk clips: Walk_F Walk_R Walk_L Walk_Bkwd

Action selectes script based on global_movement_vector calcualted by input and camera vector.

Resolution: Walk_F -> Walk_F + Walk_L blended

#### Scenario 3: change direction and start shooting

Input: W and A key, Left Click

Camera Vector: Forward

global_movement_vector: Foward_LEFT

 ---
 World/Character state: Grounded,

 External forces: none

 Style options : none

 prev_state: FOWARD, WALK_F

----

Available actions:

Walk, Run, jump, swim, shoot, hold item, pickup item,

Selected actions based on Input and prev_state: Walk, shoot

Walk clips: Walk_F Walk_R Walk_L Walk_Bkwd
Shoot Clips: no_shoot, begin_shoot (pull out gun type animation), Shoot, end_shoot

Walk Action selectes a clip based on global_movement_vector calcualted by input and camera vector.
Shoot action selectes a clip based on prev shoot state
Resolution: Walk_F -> Walk_F + Walk_L blended, no_shoot -> begin_shoot

### outside character data
	- Raycasts results
	- Input
	- Forces

Character basics -> results of everything proccesed:
- Transform3d
- current animation(s)
Derived from:
- Controller Input
- World State
		-	World Gives you:
			- collision
			- gravity
			- velocity
- Game Data:
	- game rules (can you jump?)

can we??:
Fn(Controller_Input, World_state, game_rules) -> Character_STATE

#### Claude neat thoughts:
 Requires makes dependency explicit, Serves makes the ability contract explicit, Shadows makes precedence explicit.

### Pack Interface (Extends Resource):


### Ability interface

#### Props:
- ID
- Layers: Animation clips
- Rules
- Bindings
	- example: scalar bindings for blendspaces.

# PURE SCRATCHPAD OF Q&A

Gameplay is defined by:
- global movement


## What is state?
 - A described set of set values????
  - External state:
	  - external forces ex: Enemies hitting/touching you, gravity
		-
  - Internal state:

  ## Seperate Global Movement from actions?:


## What does an action actually do????:
 - Narrow down the exact clips to play and bones to affect


 # From Lyra: 
 
 ## Linked Layer Animation Blueprint
 The [Animation Blueprint Linking system](https://dev.epicgames.com/documentation/unreal-engine/using-animation-blueprint-linking-in-unreal-engine) enables dynamic switching between different sub-sections on the Animation Graph. The main Animation Blueprint has multiple places where you can override the pose through Linked Layer Animation Blueprints. In Lyra, this means that depending on which weapon the player is holding, you can have different locomotion behavior, animation assets, or pose corrections. You can keep their functionality separate and allow multiple users to work on the animation simultaneously, or reduce dependencies between assets while still sharing the same core functionality.
