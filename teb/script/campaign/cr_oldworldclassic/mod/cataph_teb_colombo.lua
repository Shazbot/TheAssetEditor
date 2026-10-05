

--thanks to the OVN crew for letting me scavenge campaign start scripts
--tried that with Mixu's but I'm too much of a gnoblar to understand gnoblar scripts.

local function spawn_new_force()
	cm:create_force_with_general(
		"cr_teb_order_of_the_blazing_sun", -- faction_key,
        "wh_dlc04_emp_cav_knights_blazing_sun_0,wh_dlc04_emp_cav_knights_blazing_sun_0,teb_xbowmen,teb_xbowmen,teb_xbowmen,teb_pikemen,teb_republican_guard,teb_republican_guard", -- unit_list, repguard and KOTBS as special units not in the roster
		"cr_oldworld_region_magritta", -- region_key,
                405, -- x,                    -- town is 395,1145
                1455, -- y,
		"general", -- type,
		"teb_colombo", -- subtype,
		"names_name_992002",	-- first name
		"",
		"names_name_992003",	-- last name
		"", -- name4,
		true,-- make_faction_leader,
        function(cqi) -- callback
            local str = "character_cqi:" .. cqi
            cm:set_character_immortality(str, true);
            cm:set_character_unique(str, true);
        end
	)
end

local function new_game_startup()
    local teb_faction_string = "cr_teb_order_of_the_blazing_sun"
	local teb_faction = cm:get_faction(teb_faction_string)

    if not teb_faction then return end

    local to_kill_cqi = nil
    local mixer_teb_faction_leader = teb_faction:faction_leader()

	if mixer_teb_faction_leader and not mixer_teb_faction_leader:is_null_interface() then
		to_kill_cqi = mixer_teb_faction_leader:command_queue_index()
	end

    spawn_new_force()


    for _, teb_faction_region in model_pairs (teb_faction:region_list()) do
        cm:heal_garrison(teb_faction_region:cqi());
    end
    local teb_region_barracks = cm:get_region("cr_oldworld_region_svunum")
	local target_slot = teb_region_barracks:slot_list():item_at(2)
   cm:instantly_upgrade_building_in_region(target_slot, "teb_norsca_exploit_furs")
    
    cm:create_agent(
        "cr_teb_order_of_the_blazing_sun",
        "champion",
        "teb_merc_captain",
                399, -- x,                    -- town is 395,1145
                1454, -- y,
        false
    )
    cm:create_agent(
        "cr_teb_order_of_the_blazing_sun",
        "dignitary",
        "teb_priestess",
                244, -- x,                    -- needs to be in sight of the lizardmen in Albion
                1398, -- y,
        false
    )

    local unit_count = 1 -- card32 count
    local rcp = 20 -- float32 replenishment_chance_percentage
    local max_units = 1 -- int32 max_units
    local murpt = 0.1 -- float32 max_units_replenished_per_turn
    local xp_level = 0 -- card32 xp_level
    local frr = "" -- (may be empty) String faction_restricted_record
    local srr = "" -- (may be empty) String subculture_restricted_record
    local trr = "" -- (may be empty) String tech_restricted_record
    local units = {
                    "teb_ricco",
                    "teb_alcatani",
                    "teb_pirazzo",
                    "teb_leopard",
                    "teb_venators",
                    "teb_vespero",
                    "teb_marksmen",
                    "teb_besiegers",
           --         "teb_muktar",
            --        "teb_amazons",
                    "teb_cursed",
                    "teb_origo",
                    "teb_manflayers",
                    "teb_asarnil",
                    "teb_tichi",
                    --"teb_bronzino_limbered",
                    "teb_bronzino",
                    "teb_birdmen", --MIND THE COMMA IF ROC ARE INCLUDED
--ROCs 
--remember that the game WILL CRASH if you script a bunch of RORs and they all unlock at level 1,which is pertinent for ROCs
--this may also be pertinent for NWC and Colombo.

                        "teb_roc_dieterfist",
                        "teb_roc_estragon",
                        "teb_roc_verena",
                        "teb_roc_strygos",
                        "teb_roc_broswords",
                        "teb_roc_gwatch",
                        "teb_roc_kotss",
                        "teb_roc_organ",
                        "teb_roc_gunn",
                        "teb_roc_montecastello",
                        "teb_tank_ror"
        
    }


        if teb_faction:is_human() then
    cm:create_force_with_general(
				"cr_chs_slayers_of_souls",
                "wh_main_chs_mon_trolls,wh_main_chs_mon_trolls,wh3_dlc20_chs_inf_chaos_marauders_mkho_dualweapons,wh_main_chs_inf_chaos_marauders_0,wh_main_chs_inf_chaos_marauders_0,wh_main_chs_inf_chaos_marauders_0,wh_main_chs_inf_chaos_marauders_0,wh_dlc01_chs_inf_forsaken_0,wh_main_chs_cav_marauder_horsemen_0,wh_main_chs_cav_marauder_horsemen_0",
				"cr_oldworld_region_magritta",
                409, -- x,                    -- town is 395,1145
                1457, -- y,
                "general", -- type,
                "wh_main_nor_marauder_chieftain", -- subtype,
                "",	-- first name
                "",
                "",	-- last name
                "", -- name4,
				false,
				function(cqi)
					cm:disable_movement_for_character("character_cqi:" .. cqi)
				end
			)
        end

        cm:force_declare_war("cr_chs_slayers_of_souls", "cr_teb_order_of_the_blazing_sun", false, false)
        cm:force_make_trade_agreement("cr_teb_trantio", "cr_teb_order_of_the_blazing_sun")
        cm:make_diplomacy_available("cr_teb_bilbali", "cr_teb_order_of_the_blazing_sun")
        cm:make_diplomacy_available("cr_teb_miragliano", "cr_teb_order_of_the_blazing_sun")
        cm:make_diplomacy_available("cr_teb_trantio", "cr_teb_order_of_the_blazing_sun")

    for _, unit in ipairs(units) do
        cm:add_unit_to_faction_mercenary_pool(
            teb_faction,
            unit,
            "wh3_main_regiments_of_renown_pool",
            unit_count,
            rcp,
            max_units,
            murpt,
            frr,
            srr,
            trr,
            true,
            unit
        )
    end
    
    cm:callback(function()
        if to_kill_cqi then
            local str = "character_cqi:" .. to_kill_cqi
            cm:set_character_immortality(str, false)
            cm:kill_character_and_commanded_unit(str, true)
        end
    end, 0)

        cm:callback(
        function()
            cm:show_message_event(
                teb_faction_string,
                "event_feed_strings_text_wh2_scripted_event_how_they_play_title",
                "factions_screen_name_" .. teb_faction_string,
                "event_feed_strings_text_teb_how_they_play",
                true,
                3011
            );
        end,
        1
    )           
end



cm:add_first_tick_callback(
	function()
        pcall(function()
            mixer_set_faction_trait("cr_teb_order_of_the_blazing_sun", "teb_lord_trait_teb_colombo_TOW", true)
        end)
		if cm:is_new_game() then
			if cm:get_campaign_name() == "cr_oldworldclassic" then
				local ok, err =
					pcall(
					function()
						new_game_startup()
					end
				)
				if not ok then
					script_error(err)
				end
			end
		end
	end
)