----------------------------------------------------------------------------------------------
----------------------------------------------------------------------------------------------
--
--LORENZO LUPO AND THE ROOM OF CORRECT NAMES

local function teb_lorenzo_performer(faction_key)
    local faction = cm:get_faction(faction_key)
    
        local pos_x, pos_y = cm:find_valid_spawn_location_for_character_from_settlement(faction_key, "wh3_main_combi_region_luccini", false, true, 5)

        cm:create_force_with_general(
            faction_key,
            "teb_pikemen,teb_pikemen,teb_pikemen,teb_pavisiers,wh2_dlc13_emp_cav_empire_knights_ror_1",
            "wh3_main_combi_region_luccini",
            pos_x,
            pos_y,
            "general",
            "teb_lorenzo_lupo",
            "names_name_997016",
            "",
            "names_name_997017",
            "",
            false,
            function(cqi)
                local str = "character_cqi:" .. cqi
                cm:set_character_immortality(str, true)
                cm:set_character_unique(str, true)
                cm:replenish_action_points(cm:char_lookup_str(cqi))
                local character = cm:get_character_by_cqi(cqi)
                if character and character:character_subtype_key() == "teb_lorenzo_lupo" then
                    local forename = common:get_localised_string("names_name_30119882")
                    local surname = common:get_localised_string("names_name_30119892") 
                    cm:change_character_custom_name(character, forename, surname, "", "")
                    --we do this with localised strings to make the new name work for all four name groups
                    --it might not work nicely for games that use non-latin characters 
                    local leader_cqi = faction:faction_leader():command_queue_index()
                    local leader_level = cm:get_character_by_cqi(leader_cqi):rank()
                    if leader_level >= 2 then --we do this or if by any chance the faction leader is at rank 1 it goes boom, should never happen in the game but eh
                        local levels_to_gain = math.floor(leader_level / 2)
                        cm:add_agent_experience("character_cqi:" .. cqi, levels_to_gain, true) 
                    end
                end
            end
        )
end

core:add_listener(
    "teb_lorenzo_ritual",
    "RitualCompletedEvent",
    function(context)
        return context:ritual():ritual_key() == "tebuary_1_4"
    end,
    function(context)
        teb_lorenzo_performer(context:performing_faction():name())
    end,
    true
)


