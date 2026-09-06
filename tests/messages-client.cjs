const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync(require('node:path').join(__dirname, '../pages/bookings/messages.html'), 'utf8');
for (const match of html.matchAll(/<script(?![^>]*src=)[^>]*>([\s\S]*?)<\/script>/g)) new vm.Script(match[1]);
const list = { innerHTML: '' };
const context = { conversations: [{bookingId:'one',counterpartId:'talent',eventTitle:'Concert',unreadCount:2}, {bookingId:'two',counterpartId:'talent',eventTitle:'Concert'}], activeBookingId:null, document:{getElementById:id=>id==='conversationSearch'?{value:''}:list}, escapeHtml:x=>x, lucide:{createIcons(){}} };
vm.runInNewContext(html.slice(html.indexOf('    function renderConversationList()'),html.indexOf('    function mergeMessages('))+'\nrenderConversationList();', context);
assert.equal((list.innerHTML.match(/onclick=/g)||[]).length, 2, 'Separate bookings must retain separate threads');
assert.match(list.innerHTML, /2 unread/, 'Unread count must be visible');
(async () => {
 let finish;
 const c = {activeBookingId:'one',activeConversationRequest:1,fetch:()=>new Promise(r=>finish=r),activeMessages:[],mergeMessages:x=>x,renderMessages:()=>{throw Error('Stale response rendered');},loadBookingTracking(){},conversationHasMore:false};
 vm.runInNewContext(html.slice(html.indexOf('    async function refreshConversationMessagesSilently()'),html.indexOf('    function startLiveConversationUpdates(')),c);
 const pending=c.refreshConversationMessagesSilently();c.activeBookingId='two';c.activeConversationRequest=2;
 finish({ok:true,json:async()=>({messages:[{id:'old-thread-message'}],hasMore:false})});await pending;
 assert.equal(c.activeMessages.length,0,'A stale fetch must not overwrite the newly selected thread');
 console.log('PASS stale request isolation with paginated payload');
})().catch(error=>{console.error(error);process.exitCode=1;});

(async () => {
 const posts = [];
 const t = {activeBookingId:'booking-9',typingEchoTimer:null,fetch:(url,opts)=>{posts.push({url,opts});return Promise.resolve({ok:true});},setTimeout,clearTimeout};
 vm.runInNewContext(html.slice(html.indexOf('    function handleMessageListScroll()'),html.indexOf('    async function editMessage(')),t);
 t.notifyTyping();
 t.notifyTyping();
 assert.equal(posts.length,1,'Typing event must be debounced to a single POST');
 assert.match(posts[0].url, /booking-9\/typing/, 'Typing POST must target the active booking');
 assert.equal(posts[0].opts.method, 'POST', 'Typing event must use POST');
 t.notifyTyping();
 assert.equal(posts.length,1,'Typing event stays debounced while the echo timer is pending');
 clearTimeout(t.typingEchoTimer);
 console.log('PASS typing indicator debouncing and endpoint targeting');
})().catch(error=>{console.error(error);process.exitCode=1;});
