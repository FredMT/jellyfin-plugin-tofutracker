export default function (view) {
    'use strict';

    const apiPath = 'TofuTracker';
    const els = {
        form: view.querySelector('#tofuTrackerSettingsForm'),
        serverUrl: view.querySelector('#tofuTrackerServerUrl'),
        users: view.querySelector('#tofuTrackerUsers'),
        senderStatus: view.querySelector('#tofuTrackerSenderStatus'),
    };

    let pollTimer = null;
    let lastStatus = null;

    function request(type, path, body) {
        const options = { type: type, url: window.ApiClient.getUrl(apiPath + '/' + path), dataType: 'json' };
        if (body !== undefined) {
            options.data = JSON.stringify(body);
            options.contentType = 'application/json';
        }
        return window.ApiClient.ajax(options);
    }

    function toast(message) {
        if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
            window.Dashboard.alert(message);
        } else {
            window.alert(message);
        }
    }

    function errorText(error) {
        // ApiClient.ajax rejects with the Response; its body is {message} for our endpoints.
        if (error && typeof error.json === 'function') {
            return error.json().then(function (body) {
                return (body && body.message) || 'Request failed (' + error.status + ').';
            }, function () {
                return 'Request failed (' + error.status + ').';
            });
        }
        return Promise.resolve('Request failed.');
    }

    function fail(error) {
        window.Dashboard.hideLoadingMsg();
        return errorText(error).then(toast);
    }

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) {
            node.className = className;
        }
        if (text !== undefined) {
            node.textContent = text;
        }
        return node;
    }

    function button(label, onClick, extraClass) {
        const b = document.createElement('button');
        b.setAttribute('is', 'emby-button');
        b.type = 'button';
        b.className = 'raised button ' + (extraClass || '');
        const span = document.createElement('span');
        span.textContent = label;
        b.appendChild(span);
        b.addEventListener('click', onClick);
        return b;
    }

    function stateText(user) {
        if (user.pairing && user.pairing.state === 'pending') {
            return 'Waiting for approval';
        }
        if (user.linked && user.linkBroken) {
            return 'Link rejected by TofuTracker: link again';
        }
        if (user.linked) {
            return 'Linked' + (user.tofuTrackerUsername ? ' to ' + user.tofuTrackerUsername : '');
        }
        return 'Not linked';
    }

    function renderPairing(user) {
        const pairing = user.pairing;
        if (!pairing) {
            return null;
        }
        const box = el('div', 'fieldDescription');
        box.style.margin = '0.5em 0 1em 0';

        if (pairing.state === 'pending') {
            const code = el('div', '', pairing.userCode || '');
            code.style.fontSize = '1.8em';
            code.style.letterSpacing = '0.15em';
            code.style.fontWeight = 'bold';
            box.appendChild(code);

            const line = el('div');
            line.appendChild(document.createTextNode('Open '));
            const link = el('a', '', pairing.verificationUrl || '');
            link.href = pairing.verificationUrl || '#';
            link.target = '_blank';
            link.rel = 'noopener';
            link.style.textDecoration = 'underline';
            line.appendChild(link);
            line.appendChild(document.createTextNode(', sign in to TofuTracker and approve this code.'));
            box.appendChild(line);

            if (pairing.expiresAt) {
                const seconds = Math.max(0, Math.round((new Date(pairing.expiresAt).getTime() - Date.now()) / 1000));
                box.appendChild(el('div', '', 'The code expires in ' + Math.floor(seconds / 60) + ' min ' + (seconds % 60) + ' s.'));
            }
            if (pairing.message) {
                box.appendChild(el('div', '', pairing.message));
            }
            return box;
        }

        if (pairing.state === 'approved') {
            box.textContent = 'Linked to ' + (user.tofuTrackerUsername || 'TofuTracker') + '.';
            return box;
        }

        const reasons = {
            denied: 'The link request was denied.',
            expired: 'The code expired. Click Link to get a new one.',
            failed: 'Linking failed.',
        };
        box.textContent = pairing.message || reasons[pairing.state] || '';
        return box.textContent ? box : null;
    }

    function renderUser(user) {
        const row = el('div', 'listItem listItem-border');
        row.style.display = 'block';
        row.style.padding = '0.8em 0';

        const head = el('div');
        head.style.display = 'flex';
        head.style.alignItems = 'center';
        head.style.justifyContent = 'space-between';
        head.style.gap = '1em';

        const label = el('div');
        label.appendChild(el('div', '', user.name));
        label.appendChild(el('div', 'fieldDescription', stateText(user)));
        head.appendChild(label);

        const actions = el('div');
        const pending = user.pairing && user.pairing.state === 'pending';
        if (pending) {
            actions.appendChild(button('Cancel', function () { cancel(user); }));
        } else {
            actions.appendChild(button(user.linked ? 'Link again' : 'Link', function () { startLink(user); }, 'button-submit'));
            if (user.linked) {
                actions.appendChild(button('Unlink', function () { unlink(user); }));
            }
        }
        head.appendChild(actions);
        row.appendChild(head);

        const pairingBox = renderPairing(user);
        if (pairingBox) {
            row.appendChild(pairingBox);
        }
        return row;
    }

    function render(status) {
        lastStatus = status;
        if (document.activeElement !== els.serverUrl.querySelector('input') && document.activeElement !== els.serverUrl) {
            els.serverUrl.value = status.serverUrl || '';
        }

        els.users.textContent = '';
        status.users.forEach(function (user) {
            els.users.appendChild(renderUser(user));
        });

        const parts = [];
        parts.push(status.pendingEvents + ' event(s) waiting to be sent');
        if (status.lastSuccessAt) {
            parts.push('last delivery ' + new Date(status.lastSuccessAt).toLocaleString());
        }
        if (status.lastError) {
            parts.push('last error: ' + status.lastError);
        }
        els.senderStatus.textContent = parts.join(' | ');

        const anyPending = status.users.some(function (u) { return u.pairing && u.pairing.state === 'pending'; });
        schedulePoll(anyPending);
    }

    function schedulePoll(active) {
        window.clearTimeout(pollTimer);
        pollTimer = null;
        if (active) {
            pollTimer = window.setTimeout(refresh, 2000);
        }
    }

    function refresh() {
        return request('GET', 'Status').then(render, function () {
            schedulePoll(false);
        });
    }

    function startLink(user) {
        window.Dashboard.showLoadingMsg();
        request('POST', 'Link/Start', { userId: user.id }).then(function () {
            window.Dashboard.hideLoadingMsg();
            return refresh();
        }, fail);
    }

    function cancel(user) {
        request('POST', 'Link/Cancel', { userId: user.id }).then(refresh, fail);
    }

    function unlink(user) {
        const message = 'Remove the TofuTracker link of ' + user.name + ' from this server? '
            + 'To revoke it completely, also remove the connection in your TofuTracker settings.';
        if (!window.confirm(message)) {
            return;
        }
        request('POST', 'Unlink', { userId: user.id }).then(refresh, fail);
    }

    els.form.addEventListener('submit', function (event) {
        event.preventDefault();
        window.Dashboard.showLoadingMsg();
        request('POST', 'Settings', { serverUrl: els.serverUrl.value }).then(function (saved) {
            els.serverUrl.value = saved.serverUrl;
            window.Dashboard.hideLoadingMsg();
            toast('Saved.');
        }, fail);
        return false;
    });

    view.addEventListener('viewshow', function () {
        window.Dashboard.showLoadingMsg();
        refresh().then(function () {
            window.Dashboard.hideLoadingMsg();
        });
    });

    view.addEventListener('viewhide', function () {
        schedulePoll(false);
    });
}
