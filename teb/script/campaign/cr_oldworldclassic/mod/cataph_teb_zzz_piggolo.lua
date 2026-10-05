------------------------------------------
------------------------------------------
----pig it up for the AI
--this is here because it needs all the other LLs to be spawned first. Also, the faction lists would change between IME and TOW.

	teb_piggolo_factions = {
        "cr_teb_miragliano",
        "mixer_teb_new_world_colonies",
  --      "mixer_teb_catrazza",
        "cr_teb_order_of_the_blazing_sun",
        "mixer_teb_gashnag",
        "cr_teb_central_confederacy",
        "cr_teb_luccini",
        "cr_teb_pavona",
        "cr_teb_magritta"
	}

local teb_culture = "mixer_teb_southern_realms"

local function spawn_teb_piggolo_for_ai()
    -- Check if any faction of the mixer_teb_southern_realms culture is controlled by a human player
    local human_playing_teb = false
    for _, faction_key in ipairs(teb_piggolo_factions) do
        local faction = cm:get_faction(faction_key)
        if faction:is_human() and faction:culture() == teb_culture then
            human_playing_teb = true
            out("INFO: A human player is playing a faction of the mixer_teb_southern_realms culture. teb_piggolo will not be spawned for the AI.")
            break
        end
    end

    if not human_playing_teb then
        -- If no human players are playing the culture, spawn teb_piggolo for a random AI faction
        local selected_faction_key = teb_piggolo_factions[cm:random_number(#teb_piggolo_factions, 1)]
        local selected_faction = cm:get_faction(selected_faction_key)

        if selected_faction and not selected_faction:is_human() then
            local faction_cqi = selected_faction:command_queue_index()
            local faction_leader = selected_faction:faction_leader()
            local leader_cqi = faction_leader:command_queue_index()

            -- Spawn the unique agent next to the faction leader
            cm:spawn_unique_agent_at_character(faction_cqi, "teb_piggolo", leader_cqi, false)
            out("INFO: Spawned teb_piggolo for AI faction: " .. selected_faction_key)

            -- Apply settings to teb_piggolo
            cm:callback(
                function()
                    local characters = selected_faction:character_list()
                    for i = 0, characters:num_items() - 1 do
                        local character = characters:item_at(i)
                        if character:character_subtype_key() == "teb_piggolo" then
                            local cqi = character:command_queue_index()
                            local str = "character_cqi:" .. cqi
                            cm:set_character_immortality(str, true)
                            cm:set_character_unique(str, true)
                            cm:replenish_action_points(cm:char_lookup_str(cqi))
                            break
                        end
                    end
                end,
                0.1 -- Small delay to ensure the agent is spawned before we try to access it
            )
        end
    end
end

-- Run the function at the start of the game
cm:add_first_tick_callback(function()
    if cm:is_new_game() then
        spawn_teb_piggolo_for_ai()
    end
end)