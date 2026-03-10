package main

/*
#include <stdlib.h>
#include <string.h>
*/
import "C"
import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"strings"
	"sync"

	"github.com/pkg/sftp"
	"golang.org/x/crypto/ssh"
	"tailscale.com/ipn/ipnstate"
	"tailscale.com/net/netmon"
	"tailscale.com/tsnet"
)

var (
	tsServer      *tsnet.Server
	sshLn         net.Listener
	mu            sync.Mutex
	internalState string = "NotStarted"
	lastAuthUrl   string = ""
	lastErrorMsg  string = ""
)

//export StartEchoLinkNode
func StartEchoLinkNode(configDir *C.char, authKey *C.char, hostname *C.char, localIp *C.char) int {
	mu.Lock()
	defer mu.Unlock()

	if tsServer != nil {
		return 0
	}

	internalState = "Starting"
	lastErrorMsg = ""

	conf := C.GoString(configDir)
	host := C.GoString(hostname)
	key := C.GoString(authKey)
	ipStr := C.GoString(localIp)

	if host == "" {
		host = "echolink-android"
	}

	log.Printf("[Go] Starting node: Host=%s, Dir=%s, LocalIP=%s", host, conf, ipStr)

	// Dynamically register the interface getter with the IP C# gave us
	netmon.RegisterInterfaceGetter(func() ([]netmon.Interface, error) {
		var addrs []net.Addr

		// If C# successfully passed us a local IP, format it for Tailscale
		if ipStr != "" && ipStr != "127.0.0.1" {
			parsedIp := net.ParseIP(ipStr)
			if parsedIp != nil {
				// We attach a standard /24 subnet mask to the IP
				addrs = append(addrs, &net.IPNet{IP: parsedIp, Mask: net.CIDRMask(24, 32)})
			}
		}

		return []netmon.Interface{
			{
				Interface: &net.Interface{Index: 1, Name: "csharp-bridge", Flags: net.FlagUp},
				AltAddrs:  addrs, // Tailscale now knows exactly where it is on the LAN!
			},
		}, nil
	})

	// FIX: Android has no concept of a "UserConfigDir" where Tailscale can safely drop
	// its logtail state file. If we don't explicitly tell it where to put it, or disable it,
	// the Go process hard crashes with "panic: no safe place found to store log state".
	os.Setenv("TS_LOG_TARGET", "discard")   // Stop trying to upload logs to tailscale.com
	os.Setenv("TS_LOGTAIL_STATE_DIR", conf) // Even if it tries, force it to use our Android app directory

	tsServer = &tsnet.Server{
		Dir:        conf,
		Hostname:   host,
		AuthKey:    key,
		ControlURL: "https://echo-link.app", 
		Ephemeral:  false,
		Logf: func(format string, args ...any) {
			msg := fmt.Sprintf(format, args...)
			// Catch auth URLs in the logs just in case LocalClient misses it
			if strings.Contains(msg, "https://") {
				idx := strings.Index(msg, "https://")
				lastAuthUrl = msg[idx:]
				internalState = "NeedsLogin"
			}
			log.Printf("[tsnet] %s", msg)
		},
		UserLogf: func(format string, args ...any) {
			log.Printf("[tsnet-user] "+format, args...)
		},
	}

	// Disable Logtail completely to stop the "no safe place found to store log state" panic
	os.Setenv("TS_LOG_TARGET", "discard")

	// If it still panics looking for a directory, we can trick the environment
	os.Setenv("HOME", conf)
	os.Setenv("XDG_CACHE_HOME", conf)
	go func() {
		// Up() is preferred for tsnet to ensure initialization
		_, err := tsServer.Up(context.Background())
		if err == nil {
			startSftpServer()
			startPairingForwarder() // Open port 44444 to the mesh!
			internalState = "Running"
		} else {
			log.Printf("[Go] tsServer.Up error: %v", err)
			lastErrorMsg = fmt.Sprintf("tsnet.Up error: %v", err)
			internalState = "Error"
		}
	}()

	return 1
}

//export GetLastErrorMsg
func GetLastErrorMsg() *C.char {
	return C.CString(lastErrorMsg)
}

func startPairingForwarder() {
	mu.Lock()
	if tsServer == nil {
		mu.Unlock()
		return
	}
	mu.Unlock()

	// Listen on the Tailscale network IP for port 44444
	ln, err := tsServer.Listen("tcp", ":44444")
	if err != nil {
		log.Printf("[Go] Failed to listen on mesh port 44444: %v", err)
		return
	}

	log.Printf("[Go] Pairing Forwarder listening on mesh port 44444, routing to 127.0.0.1:44444")

	go func() {
		for {
			meshConn, err := ln.Accept()
			if err != nil {
				return
			}
			
			go func(c net.Conn) {
				defer c.Close()
				// Forward to the C# TcpListener running on localhost
				localConn, err := net.Dial("tcp", "127.0.0.1:44444")
				if err != nil {
					log.Printf("[Go] Failed to dial local C# pairing service: %v", err)
					return
				}
				defer localConn.Close()

				// Bidirectional copy
				go io.Copy(c, localConn)
				io.Copy(localConn, c)
			}(meshConn)
		}
	}()
}

// ... (keep startSftpServer and handleSshConn the same) ...

func startSftpServer() {
	mu.Lock()
	if sshLn != nil {
		mu.Unlock()
		return
	}
	mu.Unlock()

	ln, err := tsServer.Listen("tcp", ":2222")
	if err != nil {
		log.Printf("[Go] Failed to listen on :2222: %v", err)
		return
	}
	sshLn = ln

	config := &ssh.ServerConfig{
		NoClientAuth: true,
	}

	log.Printf("[Go] SFTP Server listening on :2222")

	for {
		conn, err := sshLn.Accept()
		if err != nil {
			return
		}
		go handleSshConn(conn, config)
	}
}

func handleSshConn(nConn net.Conn, config *ssh.ServerConfig) {
	_, chans, reqs, err := ssh.NewServerConn(nConn, config)
	if err != nil {
		return
	}
	go ssh.DiscardRequests(reqs)

	for newChannel := range chans {
		if newChannel.ChannelType() != "session" {
			newChannel.Reject(ssh.UnknownChannelType, "unknown channel type")
			continue
		}
		channel, requests, _ := newChannel.Accept()

		go func(in <-chan *ssh.Request) {
			for req := range in {
				if req.Type == "subsystem" && string(req.Payload[4:]) == "sftp" {
					req.Reply(true, nil)
					server := sftp.NewRequestServer(channel, sftp.InMemHandler())
					if err := server.Serve(); err != nil && err != io.EOF {
						log.Print("[Go] SFTP error:", err)
					}
					return
				}
				req.Reply(false, nil)
			}
		}(requests)
	}
}

type Device struct {
	Name       string `json:"Name"`
	IpAddress  string `json:"IpAddress"`
	IsOnline   bool   `json:"IsOnline"`
	DeviceType string `json:"DeviceType"`
	Os         string `json:"Os"`
}

func getStatus() (*ipnstate.Status, error) {
	if tsServer == nil {
		return nil, fmt.Errorf("not started")
	}
	lc, err := tsServer.LocalClient()
	if err != nil {
		return nil, err
	}
	return lc.Status(context.Background())
}

//export GetPeerListJson
func GetPeerListJson() *C.char {
	status, err := getStatus()
	if err != nil || status == nil {
		return C.CString("[]")
	}

	var devices []Device
	for _, peer := range status.Peer {
		ip := ""
		if len(peer.TailscaleIPs) > 0 {
			ip = peer.TailscaleIPs[0].String()
		}

		devices = append(devices, Device{
			Name:       peer.HostName,
			IpAddress:  ip,
			IsOnline:   peer.Online,
			DeviceType: "Desktop",
			Os:         peer.OS,
		})
	}

	data, _ := json.Marshal(devices)
	return C.CString(string(data))
}

//export GetBackendState
func GetBackendState() *C.char {
	if internalState == "Starting" || internalState == "Error" {
		return C.CString(internalState)
	}

	status, err := getStatus()
	if err != nil {
		// Fallback to our internal tracker if LocalClient fails
		return C.CString(internalState)
	}

	if len(status.TailscaleIPs) > 0 && status.BackendState == "Running" {
		internalState = "Running"
		return C.CString("Running")
	}

	if status.AuthURL != "" {
		lastAuthUrl = status.AuthURL
		internalState = "NeedsLogin"
		return C.CString("NeedsLogin")
	}

	if status.BackendState != "" {
		internalState = status.BackendState
	}

	return C.CString(internalState)
}

//export GetTailscaleIp
func GetTailscaleIp() *C.char {
	status, err := getStatus()
	if err != nil || status == nil || len(status.TailscaleIPs) == 0 {
		return C.CString("")
	}
	return C.CString(status.TailscaleIPs[0].String())
}

//export GetLoginUrl
func GetLoginUrl() *C.char {
	if lastAuthUrl != "" {
		return C.CString(lastAuthUrl)
	}
	status, err := getStatus()
	if err != nil || status == nil {
		return C.CString("")
	}
	return C.CString(status.AuthURL)
}

//export LogoutNode
func LogoutNode() {
	mu.Lock()
	defer mu.Unlock()
	if tsServer == nil {
		return
	}
	lc, err := tsServer.LocalClient()
	if err == nil {
		log.Printf("[Go] Triggering Logout...")
		lc.Logout(context.Background())
		internalState = "NeedsLogin"
	}
}

//export StopEchoLinkNode
func StopEchoLinkNode() {
	mu.Lock()
	defer mu.Unlock()
	internalState = "NotStarted"
	if sshLn != nil {
		sshLn.Close()
		sshLn = nil
	}
	if tsServer != nil {
		tsServer.Close()
		tsServer = nil
	}
}

func main() {}
