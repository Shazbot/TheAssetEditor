-- El Daddio unlock quest chain & radiant missions

--[[ radiant missions:

teb_eldaddio_boomer_owneverything - occupy 15 settlements from a single random culture in 1 turn (impossible); reward: dunno, whatever.
teb_eldaddio_boomer_gift - move agent to ANY (all) region; reward: 1500xp on faction leader and 100 money, boomers don't understand inflation.
teb_eldaddio_boomer_handshake - sign a military alliance with anybody; reward: 1500xp on faction leader thanks to the firm handshake

]]

--[[ further objectives:
    - more radiance with mission gen
    - track Daddio disgruntle status using a faction effect bundle
        - mission success de-gruntles Daddio, "preventing" the unrealistic missions for a while (hint: that's not true)
        - while disgruntled, do a quick survey of surroundings, find something Daddio doesn't like, or fall back on defaults like Firm Handshake
]]

local daddy_step = 0
local daddy_faction = ""


local req_02_str = [[
    mission{
        key teb_eldaddio_req_02;
        issuer TEB_ELDADDIO;

        primary_objectives_and_payload{
            heading missions_localised_title_teb_eldaddio_req_02;
            description missions_localised_description_teb_eldaddio_req_02;

            objective{
                override_text missions_OBJ_teb_eldaddio_req_02;
                type SCRIPTED;
                script_key destroy_tk_stuff;
            }
            payload{
                effect_bundle{bundle_key teb_eldaddio_req_02_dummyreward;turns 0;};
            }
        }
    }
]]

local req_03_str = [[
    mission{
        key teb_eldaddio_req_03;
        issuer TEB_ELDADDIO;

        primary_objectives_and_payload{
            heading missions_localised_title_teb_eldaddio_req_03;
            description missions_localised_description_teb_eldaddio_req_03;

            objective{
                type HAVE_AT_LEAST_X_MONEY;
                total 5000;
            }

            payload
            {
				effect_bundle{bundle_key teb_eldaddio_req_03_dummyreward;turns 0;};				
            }
        }
    }
]]--no override objective text on this mission cause it's not a SCRIPTED obj type.

local function spawn_daddio()
    -- first up, take that money
    cm:treasury_mod(daddy_faction, -5000)

    -- grab faction capital and valid spawn coords
    local faction = cm:get_faction(daddy_faction)
    --local x,y

    local region = faction:home_region()

    if region:is_null_interface() then
        local faction_leader = faction:faction_leader()
        if not faction_leader:is_null_interface() and faction_leader:has_region() and faction_leader:has_military_force() then
            region = faction_leader:region()
        end
    end

    -- if there still is not a region, just abort
    if region:is_null_interface() then
        -- failsafe general coords if no capital/faction leader are found? rare, but possible
        return false
    end
--[[
mixer_teb_catrazza
mixer_teb_colombo
mixer_teb_gashnag
mixer_teb_bilbali
mixer_teb_border_princes
mixer_teb_estalia
mixer_teb_new_world_colonies
mixer_teb_tilea
]]


    local factions_to_names = {
        ["cr_teb_miragliano"] = {"names_name_3011988", "names_name_3011989"},
        ["cr_teb_luccini"] = {"names_name_3011988", "names_name_3011989"},
 --       ["mixer_teb_bilbali"] = {"names_name_3011986", "names_name_3011987"}, --currently NWC naming group
        ["cr_teb_magritta"] = {"names_name_3011984", "names_name_3011985"},
        ["cr_teb_north_confederacy"] = {"names_name_30119881", "names_name_30119891"}, 
		["mixer_teb_gashnag"] = {"names_name_30119881", "names_name_30119891"}, 
   --     ["mixer_teb_new_world_colonies"] = {"names_name_3011986", "names_name_3011987"},
		["cr_teb_order_of_the_blazing_sun"] = {"names_name_3011986", "names_name_3011987"}
    }
    --local x,y = cm:find_valid_spawn_location_for_character_from_settlement(daddy_faction, region:name(), false, true)
    local x,y = cm:find_valid_spawn_location_for_character_from_settlement(daddy_faction, region:name(), false, true, 10)
    local names = factions_to_names[daddy_faction]
        if not names then return end        --added to avoid break in case the faction is not on the list

    -- spawn the daddio
    cm:create_force_with_general(
        daddy_faction,
        "",
        region:name(),
        x,
        y,
        "general",
        "teb_eldaddio",
        names[1],
        "",
        names[2],
        "",
        false,
        function(cqi)
            -- any traits or anything?
        end
    )
end


local function stage_two(persistent)
    if not persistent then
        -- trigger the mission before triggering the listener
        cm:trigger_custom_mission_from_string(daddy_faction, req_02_str)
    end
    
    local decisions = {
        ["895"] = true, --wh2_main_sc_teb_teb_occupation_decision_sack
		["874"] = true, --wh2_main_sc_teb_teb_occupation_decision_occupy
		["866"] = true, --wh2_main_sc_teb_teb_occupation_decision_loot
		["883"] = true --wh2_main_sc_teb_teb_occupation_decision_raze_without_occupy    --these still work cause I'm leeching off them
        --["1118"] = true -- lizardmen test		
    }

    -- check if settlement was sacked
    core:add_listener(
        "eldaddio_tk_attack",
        "CharacterPerformsSettlementOccupationDecision",
        function(context)
            out("OCCU DECISION LISTENER CONDITIONAL")
            out(context:character():faction():name())
            out(daddy_faction)
            out(tostring(context:character():faction():name() == daddy_faction))
            out(context:occupation_decision())
            out(tostring(decisions[tostring(context:occupation_decision())]))
            return context:character():faction():name() == daddy_faction and decisions[tostring(context:occupation_decision())]
        end,
        function(context)
            -- test if the previous faction was TK
            out("OCCU DECISION CALLBACK")
            local defender_char_cqi, defender_force_cqi, defender_faction_name = cm:pending_battle_cache_get_defender(1)

            out(defender_faction_name)

            if defender_faction_name ~= "rebels" then
                local defender_faction = cm:get_faction(defender_faction_name)
                if defender_faction:subculture() == "wh2_dlc09_sc_tmb_tomb_kings" then
                    out("OCCU DECISION TK")
                    local count = cm:get_saved_value("teb_eldaddio_tk_attack_count") or 0
                    out(tostring(count))
                    count = count + 1
                    cm:set_saved_value("teb_eldaddio_tk_attack_count", count)

                    out(tostring(count))

                    --if count == 1 then
	                if count == 3 then
					    out("ELDADDIO SATISFIED COUNT")
													
                        cm:complete_scripted_mission_objective(daddy_faction, "teb_eldaddio_req_02", "destroy_tk_stuff", true)

                        core:remove_listener("eldaddio_tk_attack") 
                        core:remove_listener("eldaddio_tk_attack2")
                    end
                end 
            end
        end,
        true
    )

    -- check if settlement was razed/occupied
    core:add_listener(
        "eldaddio_tk_attack2",
        "RegionFactionChangeEvent",
        function(context)
            return context:previous_faction():subculture() == "wh2_dlc09_sc_tmb_tomb_kings" and context:region():owning_faction():name() == daddy_faction
        end,
        function(context)
            local count = cm:get_saved_value("teb_eldaddio_tk_attack_count") or 0
            count = count + 1
            cm:set_saved_value("teb_eldaddio_tk_attack_count", count)

                   --if count == 1 then
	if count == 3 then 	
					    out("ELDADDIO SATISFIED COUNT")
                cm:complete_scripted_mission_objective(daddy_faction, "teb_eldaddio_req_02", "destroy_tk_stuff", true)

                core:remove_listener("eldaddio_tk_attack") 
                core:remove_listener("eldaddio_tk_attack2")
            end
        end,
        true
    )
end

core:add_listener(
    "eldaddio_mission_stage_advance",
    "MissionSucceeded",
    function(context)
        local mission = context:mission()
        local mission_key = mission:mission_record_key()
        return string.match(mission_key, "teb_eldaddio_req_")
    end,
    function(context)
        local mission = context:mission()
        local mission_key = mission:mission_record_key()
        local stage = tonumber(string.sub(mission_key, -1)) -- last character of the string

        if stage == 1 then
            -- trigger stage 2
            daddy_step = 2
            stage_two()
            return
        end

        if stage == 2 then
            -- trigger stage 3
            daddy_step = 3
            cm:trigger_custom_mission_from_string(daddy_faction, req_03_str)
            return
        end

        if stage == 3 then
            -- spawn daddy and call it a day
            daddy_step = 100
            spawn_daddio()
            return
        end
    end,
    true
)

-- loaded every session, not just new game
local function init()
    -- determine local faction key
    local faction_key = cm:get_local_faction_name()
    local faction_obj = cm:get_faction(faction_key)
    if faction_obj:subculture() == "mixer_teb_southern_realms" then
        daddy_faction = faction_key
    end

    out("ELDADDIO INIT ["..daddy_faction.."]")

    -- load up the stage 2 listeners
    if daddy_step == 2 then
        stage_two(true)
    end
end

-- trigger the first mission as early as possible
--local function new_game_init()

    core:add_listener(
        "eldaddio_req_01_trigger",
        "FactionTurnStart",
        function(context)
            return context:faction():name() == daddy_faction and context:faction():model():turn_number() == 2
        end,
        function(context)
            --out("ELDADDIO REQ 01 TRIGGER")
            -- local region_obj = cm:get_region("wh3_main_combi_region_magritta")
            -- local region_cqi = region_obj:cqi()

            --out("ELDADDIO REQ 01 TRIGGER 2")
        
            -- local faction_obj = cm:get_faction(daddy_faction)
            -- local faction_cqi = faction_obj:command_queue_index()

            --out("ELDADDIO REQ 01 TRIGGER 3")


            -- cm:trigger_mission_with_targets(faction_cqi, "teb_eldaddio_req_01", 0, 0, 0, 0, region_cqi, 0)
            
            cm:trigger_mission(daddy_faction, "teb_eldaddio_req_01", true)
            daddy_step = 1 -- on stage 1

            --out("ELDADDIO REQ 01 TRIGGER 4")
        end,
        false
    )
--end

-- main loop func
local function main()
    -- disabled for mp rn
    if cm:is_multiplayer() then
        -- do nothing
        return false
    end

    -- every-game trigger, load deets
    init()

    -- new-game-only trigger, start the mission chain
    --if cm:is_new_game() then
        --new_game_init()
    --end
end


cm:add_first_tick_callback(function() main() end)

cm:add_saving_game_callback(function(context)
    cm:save_named_value("teb_daddio_stage", daddy_step, context)
end)

cm:add_loading_game_callback(function(context)
    ---@diagnostic disable-next-line cast_local_type
    daddy_step = cm:load_named_value("teb_daddio_stage", 0, context)
end)