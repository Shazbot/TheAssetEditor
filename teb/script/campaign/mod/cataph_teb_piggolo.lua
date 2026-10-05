----------------------------------------------------------------------------------------------
----------------------------------------------------------------------------------------------
--
--OINK OINK OINK
local function teb_piggolo_performer(faction_key)
    local faction = cm:get_faction(faction_key)
    local faction_cqi = faction:command_queue_index()
    local faction_leader = faction:faction_leader()
    local leader_cqi = faction_leader:command_queue_index()

    cm:spawn_unique_agent_at_character(faction_cqi, "teb_piggolo", leader_cqi, false)

    cm:callback(
        function()
            local characters = faction:character_list()
            for i = 0, characters:num_items() - 1 do
                local character = characters:item_at(i)
                if character:character_subtype_key() == "teb_piggolo" then
                    local cqi = character:command_queue_index()
                    local str = "character_cqi:" .. cqi
                    cm:set_character_immortality(str, true)
                    cm:set_character_unique(str, true)
                    cm:replenish_action_points(cm:char_lookup_str(cqi))
                    cm:scroll_camera_from_current(true, 1.5, {character:display_position_x(), character:display_position_y(), 6, 0, 6});

   --                 local forename = common:get_localised_string("names_name_30119800")       --rename not needed with DB-side unique agent stuff, seems to work
     --               cm:change_character_custom_name(character, forename, "", "", "")

                    local leader_level = cm:get_character_by_cqi(leader_cqi):rank()
                    if leader_level >= 2 then
                        local levels_to_gain = math.floor(leader_level / 2)
                        cm:add_agent_experience("character_cqi:" .. cqi, levels_to_gain, true)
                    end
                    break
                end
            end
        end,
        0.1 -- Small delay to ensure the agent is spawned before we try to access it
    )
end

core:add_listener(
    "teb_piggolo_ritual",
    "RitualCompletedEvent",
    function(context)
        return context:ritual():ritual_key() == "tebuary_1_5"
    end,
    function(context)
        teb_piggolo_performer(context:performing_faction():name())
    end,
    true
)

