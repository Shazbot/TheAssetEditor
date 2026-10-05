--adapted from mixu's script again

local cataph_teb_defeat = {
	["teb_catrazza"] = {trait = "teb_trait_defeated_catrazza", special_subtype = nil, trait_prefix = nil},
	["teb_borgio_the_besieger"] = {trait = "teb_trait_defeated_borgio", special_subtype = nil, trait_prefix = nil},
	["teb_lucrezzia_belladonna"] = {trait = "teb_trait_defeated_lucy", special_subtype = nil, trait_prefix = nil},
	["teb_lupio"] = {trait = "teb_trait_defeated_lupio", special_subtype = nil, trait_prefix = nil},
	["teb_eldaddio"] = {trait = "teb_trait_defeated_eldaddio", special_subtype = nil, trait_prefix = nil},
	["teb_colombo"] = {trait = "teb_trait_defeated_colombo", special_subtype = nil, trait_prefix = nil},
	["teb_cadavo"] = {trait = "teb_trait_defeated_cadavo", special_subtype = nil, trait_prefix = nil},
	["teb_gashnag"] = {trait = "teb_trait_defeated_gashnag", special_subtype = nil, trait_prefix = nil},
	["teb_gausser"] = {trait = "teb_trait_defeated_gausser", special_subtype = nil, trait_prefix = nil},
	["teb_lorenzo_lupo"] = {trait = "teb_trait_defeated_lorenzo_lupo", special_subtype = nil, trait_prefix = nil}
}


local function teb_get_enemy_legendary_lords_in_last_battle(character)
	local pb = cm:model():pending_battle()
	local LL_attackers = {}
	local LL_defenders = {}
	local was_attacker = false

	local num_attackers = cm:pending_battle_cache_num_attackers()
	local num_defenders = cm:pending_battle_cache_num_defenders()

	if pb:night_battle() == true or pb:ambush_battle() == true then
		num_attackers = 1
		num_defenders = 1
	end
	
	for i = 1, num_attackers do
		local this_char_cqi, this_mf_cqi, current_faction_name = cm:pending_battle_cache_get_attacker(i)
		local char_obj = cm:model():character_for_command_queue_index(this_char_cqi)
		
		if this_char_cqi == character:cqi() then
			was_attacker = true
			break
		end
		
		if char_obj:is_null_interface() == false then
			local char_subtype = char_obj:character_subtype_key()
			
			if cataph_teb_defeat[char_subtype] ~= nil then
				table.insert(LL_attackers, char_subtype)
			end
		end
	end
	
	if was_attacker == false then
		return LL_attackers
	end
	
	for i = 1, num_defenders do
		local this_char_cqi, this_mf_cqi, current_faction_name = cm:pending_battle_cache_get_defender(i)
		local char_obj = cm:model():character_for_command_queue_index(this_char_cqi)
		
		if char_obj:is_null_interface() == false then
			local char_subtype = char_obj:character_subtype_key()
			
			if cataph_teb_defeat[char_subtype] ~= nil then
				table.insert(LL_defenders, char_subtype)
			end
		end
	end
	return LL_defenders
end

local function add_defeated_trait_listeners()
--Mixu_Log_2("Adding defeated trait listeners for cataph's TEB")
core:add_listener(
	"cataph_teb_defeat",
	"CharacterCompletedBattle",
	true,
	function(context)
		local character = context:character()
		if cm:char_is_general_with_army(character) and character:won_battle() then
			local enemy_LL = teb_get_enemy_legendary_lords_in_last_battle(character)
			
			for i = 1, #enemy_LL do
				local LL_details = cataph_teb_defeat[enemy_LL[i]]
				
				if LL_details ~= nil then
					local trait = LL_details.trait
					local special_subtype = LL_details.special_subtype
					local trait_prefix = LL_details.trait_prefix
					
					if special_subtype ~= nil then
						if character:character_subtype(special_subtype) then
							trait = trait..trait_prefix
						end
					end					
							
					cm:force_add_trait(cm:char_lookup_str(character), trait, true)
				end
			end
		end
	end,
	true
)
end


-- check to see if Cadavo completes any battle as a general
core:add_listener(
  "frogiscakedup",
  "CharacterCompletedBattle",
  function(context)
    local character = context:character()
    return cm:char_is_general_with_army(character) and character:won_battle() and character:character_subtype_key() == "teb_cadavo"
  end,
  function(context)
    -- grab cadavo, check if there's an LL enemy who was defeated, and see if any are Mazda
    local cadavo = context:character()
    local avocado_str = "character_cqi:"..cadavo:command_queue_index()

    local LL_enemies = campaign_traits:get_enemy_legendary_lords_in_last_battle(cadavo)
    for i = 1, #LL_enemies do
      local subtype = LL_enemies[i]

      -- Mazda was defeated!
      if subtype == "wh2_main_lzd_lord_mazdamundi" then
        -- hide trait messages
        cm:disable_event_feed_events(true, "", "wh_event_subcategory_character_traits", "")

        -- wait 1 second, remove the vanilla trait and reenable the trait messages
        cm:callback(function()
          cm:force_remove_trait(avocado_str, "wh2_main_trait_defeated_lord_mazdamundi")


          -- wait another second, add the cool one
          cm:callback(function()
                                cm:disable_event_feed_events(false, "", "wh_event_subcategory_character_traits", "")
           cm:force_add_trait(avocado_str, "teb_trait_defeated_mazdamundi_as_cadavo", true)
     --                Give_Trait(cadavo, "teb_trait_defeated_mazdamundi_as_cadavo")
          end, 1)
        end, 1)
      end
    end        
  end,
  true
)

cm:add_first_tick_callback(function() add_defeated_trait_listeners() end)