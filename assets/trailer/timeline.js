// Shared timing for the Loom trailer: scenes and sound cues.
// Loaded by the browser (window.LoomTimeline) and by Node (module.exports) for the soundtrack.
( function ( root, factory )
{
	const timeline = factory();
	if ( typeof module === 'object' && module.exports )
		module.exports = timeline;
	else
		root.LoomTimeline = timeline;
} )( typeof globalThis !== 'undefined' ? globalThis : this, function ()
{
	'use strict';

	const FPS = 60;
	const BPM = 100;
	/** 23 bars of 2.4 s. */
	const DURATION = 55.2;
	/** The logo lands here, on the final chord. */
	const LOGO = 49.8;

	/** Scenes in seconds; film.js stages each one, audio.mjs follows their energy. */
	const SCENES = {
		agents: [ 0, 5.4 ],
		app: [ 5.4, 17.4 ],
		workspace: [ 17.4, 24.0 ],
		modules: [ 24.0, 36.0 ],
		phone: [ 36.0, 42.6 ],
		themes: [ 42.6, 47.4 ],
		end: [ 47.4, DURATION ],
	};

	/** Beats of the agent turn in the app scene, in film seconds. */
	const TURN = {
		type: 6.0, send: 7.2, worktree: 7.7, terminal: 8.4, browser: 10.4, report: 12.6, reply: 14.0, done: 14.8,
	};

	/** Sound cues: { t, type, gain?, dur? }. Soft and physical only: air, taps, typing. */
	const sfx = [];
	const add = ( t, type, o ) => sfx.push( Object.assign( { t, type }, o || {} ) );
	const typing = ( a, b, rate ) =>
	{
		for ( let t = a, i = 0; t < b; t += 1 / rate, i++ )
			add( t, 'type', { gain: 0.6 + ( ( i * 37 ) % 10 ) / 25 } );
	};

	// Agents
	add( 0.2, 'swish', { gain: 0.35 } );
	add( 4.5, 'click' );
	add( 5.2, 'whoosh', { dur: 0.8, gain: 0.3 } );
	// The agent uses the app
	typing( TURN.type, TURN.send - 0.1, 26 );
	add( TURN.send, 'swish', { gain: 0.45 } );
	add( TURN.worktree + 0.25, 'click', { gain: 0.6 } );
	add( TURN.terminal + 0.2, 'sub', { gain: 0.45 } );
	add( TURN.browser + 0.2, 'sub', { gain: 0.45 } );
	add( TURN.report + 0.2, 'swish', { gain: 0.35 } );
	// Workspace
	add( 17.8, 'sub', { gain: 0.45 } );
	add( 19.2, 'click' );
	add( 19.3, 'whoosh', { dur: 0.75, gain: 0.25 } );
	add( 20.1, 'sub', { gain: 0.5 } );
	for ( const t of [ 20.95, 21.05, 21.15 ] )
		add( t, 'click', { gain: 0.5 } );
	add( 21.25, 'whoosh', { dur: 0.8, gain: 0.35 } );
	add( 23.8, 'whoosh', { dur: 0.7, gain: 0.25 } );
	// Modules
	add( 24.1, 'whoosh', { dur: 0.8, gain: 0.3 } );
	for ( const t of [ 26.4, 26.55, 26.7 ] )
		add( t, 'click', { gain: 0.7 } );
	add( 27.6, 'whoosh', { dur: 0.7, gain: 0.3 } );
	typing( 28.5, 30.0, 18 );
	add( 30.25, 'click' );
	add( 30.7, 'sub', { gain: 0.4 } );
	for ( let k = 0; k < 11; k++ )
		add( 31.2 + k * 0.3, 'swish', { gain: 0.22 } );
	add( 34.5, 'whoosh', { dur: 0.6, gain: 0.3 } );
	// Phone
	add( 36.0, 'swish', { gain: 0.4 } );
	add( 40.0, 'swish', { gain: 0.5 } );
	add( 40.75, 'click' );
	// Themes
	add( 42.7, 'whoosh', { dur: 0.7, gain: 0.3 } );
	for ( const t of [ 43.5, 44.1, 44.7, 45.3, 45.9 ] )
		add( t, 'swish', { gain: 0.25 } );
	// End
	add( LOGO, 'swell', { gain: 0.7 } );

	sfx.sort( ( a, b ) => a.t - b.t );

	return { DURATION, FPS, BPM, LOGO, SCENES, TURN, sfx };
} );
